using System.Text.Json;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.Checkpointing;
using ReleaseReadinessCoordinator.Data;
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

internal abstract record PendingWorkflowRequest(string RequestId);

internal sealed record PendingRemediationRequest(
    string RequestId,
    Guid? DomainRequestId)
    : PendingWorkflowRequest(RequestId);

internal sealed record PendingApprovalRequest(
    string RequestId,
    ApprovalRequest? Approval = null)
    : PendingWorkflowRequest(RequestId);

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

    public async Task<PendingWorkflowRequest> StartAsync(
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
            if (pendingRequest is null)
                throw new InvalidOperationException(
                    "The workflow completed without exposing an external request.");
            return pendingRequest;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PendingWorkflowRequest> ResumeRemediationAsync(
        Microsoft.Agents.AI.Workflows.Workflow workflow,
        string sessionId,
        PendingRemediationRequest expectedRequest,
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
            await using var run = await RestoreStreamingRunAsync(
                workflow,
                sessionId,
                cancellationToken);
            return await ContinueAfterRemediationAsync(
                run,
                expectedRequest,
                response,
                sessionId,
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PersistedHumanResponse> ResumeApprovalAsync(
        Microsoft.Agents.AI.Workflows.Workflow workflow,
        string sessionId,
        PendingApprovalRequest expectedRequest,
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
            await using var run = await RestoreStreamingRunAsync(
                workflow,
                sessionId,
                cancellationToken);
            return await ContinueAfterApprovalAsync(
                run,
                expectedRequest,
                response,
                sessionId,
                cancellationToken);
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

    private async Task<StreamingRun> RestoreStreamingRunAsync(
        Microsoft.Agents.AI.Workflows.Workflow workflow,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var checkpoint = await GetLatestCheckpointAsync(sessionId, cancellationToken);
        try
        {
            return await InProcessExecution.ResumeStreamingAsync(
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
    }

    private static async Task<PersistedHumanResponse> ContinueAfterApprovalAsync(
        StreamingRun run,
        PendingApprovalRequest expectedRequest,
        ApprovalResponse response,
        string sessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            PendingWorkflowRequest? restoredRequest = null;
            PersistedHumanResponse? terminalResponse = null;
            await foreach (var workflowEvent in run.WatchStreamAsync(cancellationToken))
            {
                switch (workflowEvent)
                {
                    case ExecutorFailedEvent failed:
                        throw ContinuationFailure(
                            ContinuationFailureKind.Incompatible,
                            sessionId,
                            $"failed in executor '{failed.ExecutorId}': {failed.Data?.Message}",
                            failed.Data);
                    case RequestInfoEvent requestEvent when restoredRequest is null:
                        restoredRequest = ToRestoredPendingWorkflowRequest(requestEvent.Request);
                        EnsureExpected(expectedRequest, restoredRequest, sessionId);
                        await run.SendResponseAsync(
                            requestEvent.Request.CreateResponse(response));
                        break;
                    case RequestInfoEvent requestEvent:
                        throw ContinuationFailure(
                            ContinuationFailureKind.Incompatible,
                            sessionId,
                            $"reached unexpected external request '{requestEvent.Request.RequestId}' after a terminal decision");
                    case WorkflowOutputEvent { Data: PersistedHumanResponse persisted }:
                        terminalResponse = persisted;
                        break;
                }

            }

            if (restoredRequest is null || terminalResponse is null)
            {
                throw ContinuationFailure(
                    ContinuationFailureKind.Incompatible,
                    sessionId,
                    "did not restore the expected request and reach one terminal response");
            }

            return terminalResponse;
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

    private static async Task<PendingWorkflowRequest> ContinueAfterRemediationAsync(
        StreamingRun run,
        PendingRemediationRequest expectedRequest,
        RemediationWorkflowResponse response,
        string sessionId,
        CancellationToken cancellationToken)
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
                    restoredRequest = await RespondToRestoredRemediationAsync(
                        run,
                        requestEvent.Request,
                        expectedRequest,
                        response,
                        sessionId);
                    continue;
                }

                nextRequest = ToPendingWorkflowRequest(requestEvent.Request);
                break;
            }

            if (restoredRequest is null)
            {
                throw ContinuationFailure(
                    ContinuationFailureKind.Incompatible,
                    sessionId,
                    "did not restore the expected remediation request");
            }

            if (run.LastCheckpoint is null)
            {
                throw ContinuationFailure(
                    ContinuationFailureKind.Incompatible,
                    sessionId,
                    "did not create a checkpoint after remediation");
            }

            if (nextRequest is null)
            {
                throw ContinuationFailure(
                    ContinuationFailureKind.Incompatible,
                    sessionId,
                    "did not reach the next external request");
            }

            return nextRequest;
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

    private static async Task<PendingWorkflowRequest> RespondToRestoredRemediationAsync(
        StreamingRun run,
        ExternalRequest request,
        PendingRemediationRequest expectedRequest,
        RemediationWorkflowResponse response,
        string sessionId)
    {
        var restoredRequest = ToRestoredPendingWorkflowRequest(request);
        EnsureExpected(expectedRequest, restoredRequest, sessionId);
        EnsureRemediationCorrelated(expectedRequest, response, sessionId);
        await run.SendResponseAsync(request.CreateResponse(response));
        return restoredRequest;
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
            return new PendingRemediationRequest(
                request.RequestId,
                domainRequestId);
        }

        if (request.PortInfo.PortId == ReleaseWorkflowPortIds.Approval
            && request.IsDataOfType<ApprovalRequest>())
        {
            var approval = request.TryGetDataAs<ApprovalRequest>(out var value) ? value : null;
            return new PendingApprovalRequest(request.RequestId, approval);
        }

        throw new WorkflowContinuationException(
            ContinuationFailureKind.Mismatched,
            $"External request '{request.RequestId}' has an unknown port or request type.");
    }

    private static PendingWorkflowRequest ToRestoredPendingWorkflowRequest(ExternalRequest request)
    {
        return request.PortInfo.PortId switch
        {
            ReleaseWorkflowPortIds.Remediation when HasPortContract<DomainRemediationRequest, RemediationWorkflowResponse>(request) =>
                new PendingRemediationRequest(request.RequestId, null),
            ReleaseWorkflowPortIds.Approval when HasPortContract<ApprovalRequest, ApprovalResponse>(request) =>
                new PendingApprovalRequest(request.RequestId),
            _ => throw new WorkflowContinuationException(
                ContinuationFailureKind.Mismatched,
                $"Restored external request '{request.RequestId}' has an unknown port or request type."),
        };
    }

    private static bool HasPortContract<TRequest, TResponse>(ExternalRequest request) =>
        request.PortInfo.RequestType.IsMatch(typeof(TRequest))
        && request.PortInfo.ResponseType.IsMatch(typeof(TResponse));

    private static void EnsureExpected(
        PendingWorkflowRequest expected,
        PendingWorkflowRequest actual,
        string sessionId)
    {
        var sameRequest = (expected, actual) switch
        {
            (PendingRemediationRequest expectedRemediation, PendingRemediationRequest actualRemediation) =>
                expectedRemediation.RequestId == actualRemediation.RequestId
                && (!expectedRemediation.DomainRequestId.HasValue
                    || !actualRemediation.DomainRequestId.HasValue
                    || expectedRemediation.DomainRequestId == actualRemediation.DomainRequestId),
            (PendingApprovalRequest expectedApproval, PendingApprovalRequest actualApproval) =>
                expectedApproval.RequestId == actualApproval.RequestId,
            _ => false,
        };
        if (sameRequest)
        {
            return;
        }

        throw ContinuationFailure(
            ContinuationFailureKind.Mismatched,
            sessionId,
            $"restored {actual.GetType().Name} request '{actual.RequestId}' instead of "
                + $"{expected.GetType().Name} request '{expected.RequestId}'");
    }

    private static void EnsureRemediationCorrelated(
        PendingRemediationRequest expectedRequest,
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
