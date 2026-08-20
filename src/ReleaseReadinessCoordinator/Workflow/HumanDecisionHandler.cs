using ReleaseReadinessCoordinator.Data;
using ReleaseReadinessCoordinator.Domain;

namespace ReleaseReadinessCoordinator.Workflow;

internal sealed class WorkflowInteractionException(Exception innerException)
    : InvalidOperationException("The workflow interaction could not be completed safely.", innerException);

public sealed record ActiveDecisionInteraction(
    ApprovalRequest Approval,
    string WorkflowRequestId);

public interface IDecisionInteractionService
{
    Task<ActiveDecisionInteraction?> GetActiveAsync(
        ReleaseId releaseId,
        CancellationToken cancellationToken = default);

    Task<bool> SubmitAsync(
        ReleaseId releaseId,
        Guid responseId,
        HumanDecision decision,
        string responder,
        string comment,
        CancellationToken cancellationToken = default);
}

internal sealed class DecisionInteractionService(
    IApplicationDataService dataService,
    ReleaseWorkflowService workflowService,
    TimeProvider timeProvider) : IDecisionInteractionService
{
    public async Task<ActiveDecisionInteraction?> GetActiveAsync(
        ReleaseId releaseId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(releaseId);
        var detail = await dataService.GetReleaseDetailAsync(releaseId, cancellationToken);
        if (detail is null)
        {
            return null;
        }

        try
        {
            if (WorkflowWaitResolver.Resolve(detail) is not PendingApprovalWait)
            {
                return null;
            }

            var restored = await workflowService.RestoreAsync(releaseId, cancellationToken);
            if (restored is not PendingApprovalWait approvalWait)
            {
                throw new InvalidOperationException(
                    "The restored workflow did not contain the active approval request.");
            }

            var approval = WorkflowWaitResolver.RequireApproval(detail);
            if (approval.Request.Id != approvalWait.ApprovalRequestId
                || approval.Request.ReleaseId != releaseId)
            {
                throw new InvalidOperationException(
                    "The restored workflow did not match the active approval request.");
            }

            return new ActiveDecisionInteraction(
                approval,
                approvalWait.WorkflowRequestId);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsSafeInteractionFailure(exception))
        {
            throw new WorkflowInteractionException(exception);
        }
    }

    public async Task<bool> SubmitAsync(
        ReleaseId releaseId,
        Guid responseId,
        HumanDecision decision,
        string responder,
        string comment,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(releaseId);
        if (responseId == Guid.Empty)
        {
            throw new ArgumentException("A response ID cannot be empty.", nameof(responseId));
        }

        decision = DomainGuard.Defined(decision, nameof(decision));
        responder = DomainGuard.Required(responder, nameof(responder));
        comment = DomainGuard.Required(comment, nameof(comment));
        var detail = await dataService.GetReleaseDetailAsync(releaseId, cancellationToken);
        if (detail is null)
        {
            return false;
        }

        if (detail.TerminalResponse is { } terminal)
        {
            return IsExactReplay(
                terminal,
                responseId,
                decision,
                responder,
                comment);
        }

        if (WorkflowWaitResolver.Resolve(detail) is not PendingApprovalWait)
        {
            return false;
        }

        var response = new ApprovalResponse(new HumanResponse(
            responseId,
            decision,
            responder,
            comment,
            new UtcInstant(timeProvider.GetUtcNow().ToUniversalTime())));
        try
        {
            await workflowService.ResumeApprovalAsync(
                releaseId,
                response,
                cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsSafeInteractionFailure(exception))
        {
            detail = await dataService.GetReleaseDetailAsync(releaseId, cancellationToken);
            if (detail?.TerminalResponse is { } concurrentTerminal)
            {
                return IsExactReplay(
                    concurrentTerminal,
                    responseId,
                    decision,
                    responder,
                    comment);
            }

            throw new WorkflowInteractionException(exception);
        }
    }

    private static bool IsExactReplay(
        PersistedHumanResponse terminal,
        Guid responseId,
        HumanDecision decision,
        string responder,
        string comment) =>
        terminal.Response.Id == responseId
            && terminal.Response.Decision == decision
            && string.Equals(
                terminal.Response.Responder,
                responder,
                StringComparison.Ordinal)
            && string.Equals(
                terminal.Response.Comment,
                comment,
                StringComparison.Ordinal);

    private static bool IsSafeInteractionFailure(Exception exception) =>
        exception is InvalidOperationException
            or AggregateException;
}

internal sealed class HumanDecisionHandler(
    ReleaseId releaseId,
    IApplicationDataService dataService)
{
    private readonly ReleaseId _releaseId =
        releaseId ?? throw new ArgumentNullException(nameof(releaseId));
    private readonly IApplicationDataService _dataService =
        dataService ?? throw new ArgumentNullException(nameof(dataService));

    public async Task<PersistedHumanResponse> HandleAsync(
        HumanResponse response,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        var detail = await _dataService.GetReleaseDetailAsync(_releaseId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Release '{_releaseId}' does not exist.");

        if (detail.TerminalResponse?.Response.Id == response.Id)
        {
            if (detail.TerminalResponse.Response != response)
            {
                throw new InvalidOperationException(
                    $"Human response ID '{response.Id}' was already used with different content.");
            }

            return detail.TerminalResponse;
        }

        if (detail.Release.Phase is not ProcessPhase.WaitingForApproval)
        {
            throw new ApplicationDataConflictException(
                ApplicationDataConflictKind.InvalidState,
                "A human response requires a release waiting for approval.");
        }

        var activeRequest = WorkflowWaitResolver.RequireApproval(detail).Request;
        var nextSequence = detail.Timeline.IsEmpty ? 1 : detail.Timeline[^1].Sequence + 1;
        return await _dataService.SaveHumanResponseAsync(
            _releaseId,
            activeRequest.Id,
            response,
            new TimelineEntry(
                Guid.NewGuid(),
                _releaseId,
                nextSequence,
                TimelineEntryKind.HumanResponseAccepted,
                $"Human response '{response.Decision}' accepted from '{response.Responder}'.",
                response.RespondedAt),
            RoundAggregator.OperationKey(_releaseId, $"human-response:{response.Id:N}"),
            cancellationToken);
    }
}
