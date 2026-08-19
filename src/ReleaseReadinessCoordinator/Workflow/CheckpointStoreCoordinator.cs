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

internal abstract record PendingWorkflowWait(string WorkflowRequestId);

internal sealed record PendingRemediationWait(
    string WorkflowRequestId,
    Guid? RemediationRequestId)
    : PendingWorkflowWait(WorkflowRequestId);

internal sealed record PendingApprovalWait(
    string WorkflowRequestId,
    ApprovalRequest? Approval = null)
    : PendingWorkflowWait(WorkflowRequestId);

internal sealed class CheckpointStoreCoordinator : IDisposable
{
    private readonly FileSystemJsonCheckpointStore _store;
    private readonly CheckpointManager _manager;
    private readonly SemaphoreSlim _operationGate = new(1, 1);
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

    public async Task<PendingWorkflowWait> StartAsync(
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

            PendingWorkflowWait? pendingWait = null;
            await foreach (var workflowEvent in run
                .WatchStreamAsync(blockOnPendingRequest: false, cancellationToken))
            {
                if (workflowEvent is ExecutorFailedEvent failed)
                {
                    throw new InvalidOperationException(
                        $"Workflow failed in executor '{failed.ExecutorId}': {failed.Data?.Message}",
                        failed.Data);
                }

                if (workflowEvent is not RequestInfoEvent requestEvent)
                {
                    continue;
                }

                if (pendingWait is not null)
                {
                    throw new InvalidOperationException(
                        "A workflow wait must expose exactly one pending external request.");
                }

                pendingWait = ToPendingWorkflowWait(requestEvent.Request);
            }
            if (run.LastCheckpoint is null)
                throw new InvalidOperationException(
                    "The workflow reached an external wait without creating a checkpoint.");
            if (pendingWait is null)
                throw new InvalidOperationException(
                    "The workflow completed without exposing an external request.");
            return pendingWait;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<T> ExecuteExclusivelyAsync<T>(
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(operation);

        await _operationGate.WaitAsync(cancellationToken);
        try
        {
            return await operation(cancellationToken);
        }
        finally
        {
            _operationGate.Release();
        }
    }

    public async Task<PendingWorkflowWait> ResumeRemediationAsync(
        Microsoft.Agents.AI.Workflows.Workflow workflow,
        string sessionId,
        PendingRemediationWait expectedWait,
        RemediationWorkflowResponse response,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(expectedWait);
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
                expectedWait,
                response,
                sessionId,
                cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PendingWorkflowWait> RestoreAsync(
        Microsoft.Agents.AI.Workflows.Workflow workflow,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            await using var run = await RestoreStreamingRunAsync(
                workflow,
                sessionId,
                cancellationToken);
            PendingWorkflowWait? restoredWait = null;
            await foreach (var workflowEvent in run
                .WatchStreamAsync(blockOnPendingRequest: false, cancellationToken))
            {
                if (workflowEvent is not RequestInfoEvent requestEvent)
                {
                    continue;
                }

                if (restoredWait is not null)
                {
                    throw ContinuationFailure(
                        ContinuationFailureKind.Incompatible,
                        sessionId,
                        "restored more than one pending external request");
                }

                restoredWait = ToRestoredPendingWorkflowWait(requestEvent.Request);
            }

            return restoredWait ?? throw ContinuationFailure(
                ContinuationFailureKind.Incompatible,
                sessionId,
                "did not restore a pending external request");
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PersistedHumanResponse> ResumeApprovalAsync(
        Microsoft.Agents.AI.Workflows.Workflow workflow,
        string sessionId,
        PendingApprovalWait expectedWait,
        ApprovalResponse response,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(workflow);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(expectedWait);
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
                expectedWait,
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
        _operationGate.Dispose();
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
        PendingApprovalWait expectedWait,
        ApprovalResponse response,
        string sessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            PendingWorkflowWait? restoredWait = null;
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
                    case RequestInfoEvent requestEvent when restoredWait is null:
                        restoredWait = ToRestoredPendingWorkflowWait(requestEvent.Request);
                        EnsureExpectedWait(expectedWait, restoredWait, sessionId);
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

            if (restoredWait is null || terminalResponse is null)
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

    private static async Task<PendingWorkflowWait> ContinueAfterRemediationAsync(
        StreamingRun run,
        PendingRemediationWait expectedWait,
        RemediationWorkflowResponse response,
        string sessionId,
        CancellationToken cancellationToken)
    {
        try
        {
            PendingWorkflowWait? restoredWait = null;
            PendingWorkflowWait? nextWait = null;
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

                if (restoredWait is null)
                {
                    restoredWait = await RespondToRestoredRemediationAsync(
                        run,
                        requestEvent.Request,
                        expectedWait,
                        response,
                        sessionId);
                    continue;
                }

                nextWait = ToPendingWorkflowWait(requestEvent.Request);
                break;
            }

            if (restoredWait is null)
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

            if (nextWait is null)
            {
                throw ContinuationFailure(
                    ContinuationFailureKind.Incompatible,
                    sessionId,
                    "did not reach the next external request");
            }

            return nextWait;
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

    private static async Task<PendingWorkflowWait> RespondToRestoredRemediationAsync(
        StreamingRun run,
        ExternalRequest request,
        PendingRemediationWait expectedWait,
        RemediationWorkflowResponse response,
        string sessionId)
    {
        var restoredWait = ToRestoredPendingWorkflowWait(request);
        EnsureExpectedWait(expectedWait, restoredWait, sessionId);
        EnsureRemediationCorrelated(expectedWait, response, sessionId);
        await run.SendResponseAsync(request.CreateResponse(response));
        return restoredWait;
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

    private static PendingWorkflowWait ToPendingWorkflowWait(ExternalRequest request)
    {
        if (request.PortInfo.PortId == ReleaseWorkflowPortIds.Remediation
            && request.IsDataOfType<DomainRemediationRequest>())
        {
            var remediationRequestId = request.TryGetDataAs<DomainRemediationRequest>(out var domainRequest)
                ? domainRequest.Id
                : (Guid?)null;
            return new PendingRemediationWait(
                request.RequestId,
                remediationRequestId);
        }

        if (request.PortInfo.PortId == ReleaseWorkflowPortIds.Approval
            && request.IsDataOfType<ApprovalRequest>())
        {
            var approval = request.TryGetDataAs<ApprovalRequest>(out var value) ? value : null;
            return new PendingApprovalWait(request.RequestId, approval);
        }

        throw new WorkflowContinuationException(
            ContinuationFailureKind.Mismatched,
            $"External request '{request.RequestId}' has an unknown port or request type.");
    }

    private static PendingWorkflowWait ToRestoredPendingWorkflowWait(ExternalRequest request)
    {
        return request.PortInfo.PortId switch
        {
            ReleaseWorkflowPortIds.Remediation when HasPortContract<DomainRemediationRequest, RemediationWorkflowResponse>(request) =>
                new PendingRemediationWait(request.RequestId, null),
            ReleaseWorkflowPortIds.Approval when HasPortContract<ApprovalRequest, ApprovalResponse>(request) =>
                new PendingApprovalWait(request.RequestId),
            _ => throw new WorkflowContinuationException(
                ContinuationFailureKind.Mismatched,
                $"Restored external request '{request.RequestId}' has an unknown port or request type."),
        };
    }

    private static bool HasPortContract<TRequest, TResponse>(ExternalRequest request) =>
        request.PortInfo.RequestType.IsMatch(typeof(TRequest))
        && request.PortInfo.ResponseType.IsMatch(typeof(TResponse));

    private static void EnsureExpectedWait(
        PendingWorkflowWait expected,
        PendingWorkflowWait actual,
        string sessionId)
    {
        var sameWait = (expected, actual) switch
        {
            (PendingRemediationWait expectedRemediation, PendingRemediationWait actualRemediation) =>
                expectedRemediation.WorkflowRequestId == actualRemediation.WorkflowRequestId
                && (!expectedRemediation.RemediationRequestId.HasValue
                    || !actualRemediation.RemediationRequestId.HasValue
                    || expectedRemediation.RemediationRequestId == actualRemediation.RemediationRequestId),
            (PendingApprovalWait expectedApproval, PendingApprovalWait actualApproval) =>
                expectedApproval.WorkflowRequestId == actualApproval.WorkflowRequestId,
            _ => false,
        };
        if (sameWait)
        {
            return;
        }

        throw ContinuationFailure(
            ContinuationFailureKind.Mismatched,
            sessionId,
            $"restored {actual.GetType().Name} with workflow request '{actual.WorkflowRequestId}' instead of "
                + $"{expected.GetType().Name} with workflow request '{expected.WorkflowRequestId}'");
    }

    private static void EnsureRemediationCorrelated(
        PendingRemediationWait expectedWait,
        RemediationWorkflowResponse response,
        string sessionId)
    {
        if (!expectedWait.RemediationRequestId.HasValue
            || expectedWait.RemediationRequestId.Value != response.Submission.RequestId)
        {
            var activeRemediationRequestId =
                expectedWait.RemediationRequestId?.ToString() ?? "<unavailable>";
            throw ContinuationFailure(
                ContinuationFailureKind.Mismatched,
                sessionId,
                $"received remediation for request '{response.Submission.RequestId}' instead of active request '{activeRemediationRequestId}'");
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
