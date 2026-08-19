using ReleaseReadinessCoordinator.Data;
using ReleaseReadinessCoordinator.Domain;

namespace ReleaseReadinessCoordinator.Workflow;

public sealed record ActiveDecisionInteraction
{
    public ActiveDecisionInteraction(
        ApprovalRequest approval,
        string workflowRequestId)
    {
        ArgumentNullException.ThrowIfNull(approval);
        if (approval.Snapshot.ReleaseId != approval.Request.ReleaseId
            || approval.Snapshot.Id != approval.Request.SnapshotId)
        {
            throw new ArgumentException(
                "An active decision interaction must contain one consistent approval request.",
                nameof(approval));
        }

        Approval = approval;
        WorkflowRequestId = DomainGuard.Required(
            workflowRequestId,
            nameof(workflowRequestId));
    }

    public ApprovalRequest Approval { get; }

    public string WorkflowRequestId { get; }
}

public enum DecisionLoadOutcome
{
    Active = 1,
    ReleaseNotFound = 2,
    NoLongerActive = 3,
    TechnicalFailure = 4,
}

public sealed record DecisionLoadResult
{
    private DecisionLoadResult(
        DecisionLoadOutcome outcome,
        ActiveDecisionInteraction? interaction)
    {
        Outcome = DomainGuard.Defined(outcome, nameof(outcome));
        Interaction = interaction;
        if ((outcome is DecisionLoadOutcome.Active) != (interaction is not null))
        {
            throw new ArgumentException(
                "Only an active decision load result can contain an interaction.");
        }
    }

    public DecisionLoadOutcome Outcome { get; }

    public ActiveDecisionInteraction? Interaction { get; }

    public static DecisionLoadResult Active(ActiveDecisionInteraction interaction) =>
        new(DecisionLoadOutcome.Active, interaction);

    public static DecisionLoadResult From(DecisionLoadOutcome outcome) =>
        new(outcome, null);
}

public sealed record DecisionSubmission
{
    public DecisionSubmission(
        Guid responseId,
        HumanDecision decision,
        string responder,
        string comment)
    {
        if (responseId == Guid.Empty)
        {
            throw new ArgumentException("A response ID cannot be empty.", nameof(responseId));
        }

        ResponseId = responseId;
        Decision = DomainGuard.Defined(decision, nameof(decision));
        Responder = DomainGuard.Required(responder, nameof(responder));
        Comment = DomainGuard.Required(comment, nameof(comment));
    }

    public Guid ResponseId { get; }

    public HumanDecision Decision { get; }

    public string Responder { get; }

    public string Comment { get; }
}

public enum DecisionSubmitOutcome
{
    Succeeded = 1,
    ExactReplay = 2,
    ReleaseNotFound = 3,
    NoLongerActive = 4,
    ResponseConflict = 5,
    TechnicalFailure = 6,
}

public interface IDecisionInteractionService
{
    Task<DecisionLoadResult> LoadAsync(
        ReleaseId releaseId,
        CancellationToken cancellationToken = default);

    Task<DecisionSubmitOutcome> SubmitAsync(
        ReleaseId releaseId,
        DecisionSubmission submission,
        CancellationToken cancellationToken = default);
}

internal sealed class DecisionInteractionService(
    IApplicationDataService dataService,
    ReleaseWorkflowService workflowService,
    TimeProvider timeProvider) : IDecisionInteractionService
{
    public async Task<DecisionLoadResult> LoadAsync(
        ReleaseId releaseId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(releaseId);
        var detail = await dataService.GetReleaseDetailAsync(releaseId, cancellationToken);
        if (detail is null)
        {
            return DecisionLoadResult.From(DecisionLoadOutcome.ReleaseNotFound);
        }

        if (!IsWaitingForApproval(detail))
        {
            return DecisionLoadResult.From(DecisionLoadOutcome.NoLongerActive);
        }

        try
        {
            var restored = await workflowService.RestoreAsync(releaseId, cancellationToken);
            if (restored is not PendingApprovalWait { Approval: { } approval } approvalWait
                || approval.Request.ReleaseId != releaseId)
            {
                return DecisionLoadResult.From(DecisionLoadOutcome.TechnicalFailure);
            }

            return DecisionLoadResult.Active(
                new ActiveDecisionInteraction(
                    approval,
                    approvalWait.WorkflowRequestId));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsSafeInteractionFailure(exception))
        {
            return DecisionLoadResult.From(DecisionLoadOutcome.TechnicalFailure);
        }
    }

    public async Task<DecisionSubmitOutcome> SubmitAsync(
        ReleaseId releaseId,
        DecisionSubmission submission,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(releaseId);
        ArgumentNullException.ThrowIfNull(submission);
        var detail = await dataService.GetReleaseDetailAsync(releaseId, cancellationToken);
        if (detail is null)
        {
            return DecisionSubmitOutcome.ReleaseNotFound;
        }

        if (detail.TerminalResponse is { } terminal)
        {
            return TerminalOutcome(terminal, submission);
        }

        if (!IsWaitingForApproval(detail))
        {
            return DecisionSubmitOutcome.NoLongerActive;
        }

        var response = new ApprovalResponse(new HumanResponse(
            submission.ResponseId,
            submission.Decision,
            submission.Responder,
            submission.Comment,
            new UtcInstant(timeProvider.GetUtcNow().ToUniversalTime())));
        try
        {
            await workflowService.ResumeApprovalAsync(
                releaseId,
                response,
                cancellationToken);
            return DecisionSubmitOutcome.Succeeded;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (IsSafeInteractionFailure(exception))
        {
            detail = await dataService.GetReleaseDetailAsync(releaseId, cancellationToken);
            return detail?.TerminalResponse is { } concurrentTerminal
                ? TerminalOutcome(concurrentTerminal, submission)
                : DecisionSubmitOutcome.TechnicalFailure;
        }
    }

    private static bool IsWaitingForApproval(ReleaseDetailProjection detail) =>
        detail.Release.Phase is ProcessPhase.WaitingForApproval
        && detail.TerminalResponse is null
        && detail.WorkflowCorrelation is
        {
            PendingRequestKind: WorkflowRequestKind.Approval,
        };

    private static DecisionSubmitOutcome TerminalOutcome(
        PersistedHumanResponse terminal,
        DecisionSubmission submission)
    {
        if (terminal.Response.Id != submission.ResponseId)
        {
            return DecisionSubmitOutcome.NoLongerActive;
        }

        return terminal.Response.Decision == submission.Decision
            && string.Equals(
                terminal.Response.Responder,
                submission.Responder,
                StringComparison.Ordinal)
            && string.Equals(
                terminal.Response.Comment,
                submission.Comment,
                StringComparison.Ordinal)
                ? DecisionSubmitOutcome.ExactReplay
                : DecisionSubmitOutcome.ResponseConflict;
    }

    private static bool IsSafeInteractionFailure(Exception exception) =>
        exception is WorkflowContinuationException
            or ApplicationDataConflictException
            or InvalidOperationException
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

        var activeRequest = detail.HumanDecisionRequests
            .OrderBy(request => request.CreatedAt)
            .LastOrDefault()
            ?? throw new InvalidOperationException("A human response requires a durable approval request.");
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
