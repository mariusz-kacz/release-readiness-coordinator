using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using DomainRemediationRequest = ReleaseReadinessCoordinator.Domain.RemediationRequest;

namespace ReleaseReadinessCoordinator.Workflow;

internal enum ContinuationFailureKind
{
    Missing = 1,
    Corrupt = 2,
    Mismatched = 3,
    Incompatible = 4,
}

internal sealed class WorkflowContinuationException : InvalidOperationException
{
    public WorkflowContinuationException(
        ContinuationFailureKind kind,
        string message,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }

    public ContinuationFailureKind Kind { get; }
}

internal sealed record PendingWorkflowRequest(
    string RequestId,
    ExternalWaitKind Kind,
    string PortId,
    Guid? DomainRequestId = null);

internal sealed record WorkflowStartResult(PendingWorkflowRequest PendingRequest);

internal sealed record WorkflowResumeResult<TResponse>(
    PendingWorkflowRequest RestoredRequest,
    TResponse Output);

internal sealed record WorkflowRemediationResumeResult(PendingWorkflowRequest NextRequest);

internal sealed class CheckpointStoreCoordinator : IDisposable
{
    private readonly FileSystemJsonCheckpointStore _store;
    private readonly CheckpointManager _manager;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _disposed;

    public CheckpointStoreCoordinator(DirectoryInfo directory)
    {
        ArgumentNullException.ThrowIfNull(directory);

        _store = new FileSystemJsonCheckpointStore(directory);
        _manager = CheckpointManager.CreateJson(
            _store,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
    }

    public async Task<WorkflowStartResult> StartAsync(
        Microsoft.Agents.AI.Workflows.Workflow workflow,
        EvaluationRoundStart input,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var run = await InProcessExecution.RunStreamingAsync(
                workflow,
                input,
                _manager,
                sessionId,
                cancellationToken);

            PendingWorkflowRequest? pendingRequest = null;
            await foreach (var workflowEvent in run
                .WatchStreamAsync(blockOnPendingRequest: false, cancellationToken))
            {
                if (workflowEvent is not RequestInfoEvent requestEvent)
                {
                    continue;
                }

                if (pendingRequest is not null)
                {
                    throw new InvalidOperationException(
                        "A workflow wait must expose exactly one pending external request.");
                }

                pendingRequest = ToPendingWorkflowRequest(requestEvent.Request);
            }
            if (run.LastCheckpoint is null)
                throw new InvalidOperationException(
                    "The workflow reached an external wait without creating a checkpoint.");
            return new WorkflowStartResult(
                pendingRequest
                    ?? throw new InvalidOperationException(
                        "The workflow completed without exposing an external request."));
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<WorkflowRemediationResumeResult> ResumeRemediationAsync(
        Microsoft.Agents.AI.Workflows.Workflow workflow,
        string sessionId,
        PendingWorkflowRequest expectedRequest,
        RemediationWorkflowResponse response,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(expectedRequest);
        ArgumentNullException.ThrowIfNull(response);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var checkpoint = await GetLatestCheckpointAsync(sessionId, cancellationToken);
            StreamingRun run;
            try
            {
                run = await InProcessExecution.ResumeStreamingAsync(
                    workflow,
                    checkpoint,
                    _manager,
                    cancellationToken);
            }
            catch (Exception exception) when (IsCorrupt(exception))
            {
                throw ContinuationFailure(
                    ContinuationFailureKind.Corrupt,
                    sessionId,
                    "could not be deserialized",
                    exception);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw ContinuationFailure(
                    ContinuationFailureKind.Incompatible,
                    sessionId,
                    "is incompatible with the rebuilt workflow",
                    exception);
            }

            await using (run)
            {
                try
                {
                    PendingWorkflowRequest? restoredRequest = null;
                    PendingWorkflowRequest? nextRequest = null;
                    await foreach (var workflowEvent in run.WatchStreamAsync(cancellationToken))
                    {
                        if (workflowEvent is ExecutorFailedEvent failed)
                        {
                            throw ContinuationFailure(
                                ContinuationFailureKind.Incompatible,
                                sessionId,
                                $"failed in executor '{failed.ExecutorId}': {failed.Data?.Message}",
                                failed.Data);
                        }

                        if (workflowEvent is not RequestInfoEvent requestEvent)
                        {
                            continue;
                        }

                        if (restoredRequest is null)
                        {
                            restoredRequest = ToRestoredPendingWorkflowRequest(requestEvent.Request);
                            EnsureExpected(expectedRequest, restoredRequest, sessionId);
                            EnsureRemediationCorrelated(expectedRequest, response, sessionId);
                            await run.SendResponseAsync(
                                requestEvent.Request.CreateResponse(response));
                            continue;
                        }

                        nextRequest = ToPendingWorkflowRequest(requestEvent.Request);
                        break;
                    }

                    _ = restoredRequest
                        ?? throw ContinuationFailure(
                            ContinuationFailureKind.Incompatible,
                            sessionId,
                            "did not restore the expected remediation request");
                    _ = run.LastCheckpoint
                        ?? throw ContinuationFailure(
                            ContinuationFailureKind.Incompatible,
                            sessionId,
                            "did not create a checkpoint after remediation");
                    return new WorkflowRemediationResumeResult(
                        nextRequest
                            ?? throw ContinuationFailure(
                                ContinuationFailureKind.Incompatible,
                                sessionId,
                                "did not reach the next external request"));
                }
                catch (WorkflowContinuationException)
                {
                    throw;
                }
                catch (Exception exception) when (IsCorrupt(exception))
                {
                    throw ContinuationFailure(
                        ContinuationFailureKind.Corrupt,
                        sessionId,
                        "could not be deserialized",
                        exception);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    throw ContinuationFailure(
                        ContinuationFailureKind.Incompatible,
                        sessionId,
                        "could not resume remediation",
                        exception);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<WorkflowResumeResult<ApprovalResponse>> ResumeApprovalAsync(
        Microsoft.Agents.AI.Workflows.Workflow workflow,
        string sessionId,
        PendingWorkflowRequest expectedRequest,
        ApprovalResponse response,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(expectedRequest);
        ArgumentNullException.ThrowIfNull(response);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            var checkpoint = await GetLatestCheckpointAsync(sessionId, cancellationToken);
            StreamingRun run;
            try
            {
                run = await InProcessExecution.ResumeStreamingAsync(
                    workflow,
                    checkpoint,
                    _manager,
                    cancellationToken);
            }
            catch (Exception exception) when (IsCorrupt(exception))
            {
                throw ContinuationFailure(
                    ContinuationFailureKind.Corrupt,
                    sessionId,
                    "could not be deserialized",
                    exception);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                throw ContinuationFailure(
                    ContinuationFailureKind.Incompatible,
                    sessionId,
                    "is incompatible with the rebuilt workflow",
                    exception);
            }

            await using (run)
            {
                try
                {
                    PendingWorkflowRequest? restoredRequest = null;
                    ApprovalResponse? output = null;
                    await foreach (var workflowEvent in run.WatchStreamAsync(cancellationToken))
                    {
                        switch (workflowEvent)
                        {
                            case RequestInfoEvent requestEvent when restoredRequest is null:
                                restoredRequest = ToRestoredPendingWorkflowRequest(requestEvent.Request);
                                EnsureExpected(expectedRequest, restoredRequest, sessionId);
                                await run.SendResponseAsync(
                                    requestEvent.Request.CreateResponse(response));
                                break;
                            case RequestInfoEvent:
                                throw ContinuationFailure(
                                    ContinuationFailureKind.Incompatible,
                                    sessionId,
                                    "produced another external request while completing approval");
                            case WorkflowOutputEvent { Data: ApprovalResponse approval }:
                                output = approval;
                                break;
                        }
                    }

                    if (restoredRequest is null || output is null)
                    {
                        throw ContinuationFailure(
                            ContinuationFailureKind.Incompatible,
                            sessionId,
                            "did not restore the expected request and terminal response");
                    }

                    return new WorkflowResumeResult<ApprovalResponse>(restoredRequest, output);
                }
                catch (WorkflowContinuationException)
                {
                    throw;
                }
                catch (Exception exception) when (IsCorrupt(exception))
                {
                    throw ContinuationFailure(
                        ContinuationFailureKind.Corrupt,
                        sessionId,
                        "could not be deserialized",
                        exception);
                }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    throw ContinuationFailure(
                        ContinuationFailureKind.Incompatible,
                        sessionId,
                        "could not be resumed by the rebuilt workflow",
                        exception);
                }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _store.Dispose();
        _gate.Dispose();
    }

    private async Task<CheckpointInfo> GetLatestCheckpointAsync(
        string sessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _manager.GetLatestCheckpointAsync(sessionId, cancellationToken)
                ?? throw ContinuationFailure(
                    ContinuationFailureKind.Missing,
                    sessionId,
                    "does not exist");
        }
        catch (WorkflowContinuationException)
        {
            throw;
        }
        catch (Exception exception) when (IsCorrupt(exception))
        {
            throw ContinuationFailure(
                ContinuationFailureKind.Corrupt,
                sessionId,
                "could not be read",
                exception);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            throw ContinuationFailure(
                ContinuationFailureKind.Missing,
                sessionId,
                "could not be found",
                exception);
        }
    }

    private static PendingWorkflowRequest ToPendingWorkflowRequest(ExternalRequest request)
    {
        if (request.PortInfo.PortId == ReleaseWorkflowPortIds.Remediation
            && request.IsDataOfType<DomainRemediationRequest>())
        {
            var domainRequestId = request.TryGetDataAs<DomainRemediationRequest>(out var domainRequest)
                ? domainRequest.Id
                : (Guid?)null;
            return new PendingWorkflowRequest(
                request.RequestId,
                ExternalWaitKind.Remediation,
                request.PortInfo.PortId,
                domainRequestId);
        }

        if (request.PortInfo.PortId == ReleaseWorkflowPortIds.Approval
            && request.IsDataOfType<ApprovalRequest>())
        {
            return new PendingWorkflowRequest(
                request.RequestId,
                ExternalWaitKind.Approval,
                request.PortInfo.PortId);
        }

        throw new WorkflowContinuationException(
            ContinuationFailureKind.Mismatched,
            $"External request '{request.RequestId}' has an unknown port or request type.");
    }

    private static PendingWorkflowRequest ToRestoredPendingWorkflowRequest(ExternalRequest request)
    {
        var kind = request.PortInfo.PortId switch
        {
            ReleaseWorkflowPortIds.Remediation => ExternalWaitKind.Remediation,
            ReleaseWorkflowPortIds.Approval => ExternalWaitKind.Approval,
            _ => throw new WorkflowContinuationException(
                ContinuationFailureKind.Mismatched,
                $"Restored external request '{request.RequestId}' has an unknown port."),
        };
        return new PendingWorkflowRequest(request.RequestId, kind, request.PortInfo.PortId);
    }

    private static void EnsureExpected(
        PendingWorkflowRequest expected,
        PendingWorkflowRequest actual,
        string sessionId)
    {
        if (expected != actual)
        {
            var sameRequest = expected.RequestId == actual.RequestId
                && expected.Kind == actual.Kind
                && expected.PortId == actual.PortId
                && (!expected.DomainRequestId.HasValue
                    || !actual.DomainRequestId.HasValue
                    || expected.DomainRequestId == actual.DomainRequestId);
            if (sameRequest)
            {
                return;
            }

            throw ContinuationFailure(
                ContinuationFailureKind.Mismatched,
                sessionId,
                $"restored request '{actual.RequestId}' ({actual.Kind}) instead of "
                    + $"'{expected.RequestId}' ({expected.Kind})");
        }
    }

    private static void EnsureRemediationCorrelated(
        PendingWorkflowRequest expectedRequest,
        RemediationWorkflowResponse response,
        string sessionId)
    {
        if (!expectedRequest.DomainRequestId.HasValue
            || expectedRequest.DomainRequestId.Value != response.Submission.RequestId)
        {
            var activeRequestId = expectedRequest.DomainRequestId?.ToString() ?? "<unavailable>";
            throw ContinuationFailure(
                ContinuationFailureKind.Mismatched,
                sessionId,
                $"received remediation for request '{response.Submission.RequestId}' instead of active request '{activeRequestId}'");
        }
    }

    private static WorkflowContinuationException ContinuationFailure(
        ContinuationFailureKind kind,
        string sessionId,
        string reason,
        Exception? innerException = null) =>
        new(
            kind,
            $"Workflow continuation for session '{sessionId}' {reason}; a new workflow was not started.",
            innerException);

    private static bool IsCorrupt(Exception exception) =>
        exception is JsonException
        || (exception.InnerException is not null && IsCorrupt(exception.InnerException));
}
