using System.Collections.Immutable;
using ReleaseReadinessCoordinator.Data;
using ReleaseReadinessCoordinator.Domain;

namespace ReleaseReadinessCoordinator.Workflow;

public sealed record ActiveRemediationInteraction(
    Release Release,
    RemediationRequest Request,
    ImmutableDictionary<EvidenceKind, EvidenceRecord> CurrentEvidence,
    string CorrelationToken);

public interface IRemediationInteractionService
{
    Task<ActiveRemediationInteraction?> GetActiveAsync(
        ReleaseId releaseId,
        CancellationToken cancellationToken = default);

    Task<bool> SubmitAsync(
        ReleaseId releaseId,
        string correlationToken,
        IReadOnlyCollection<EvidenceRecord> evidenceReplacements,
        IReadOnlyCollection<ReadinessCheck> explicitlySelectedChecks,
        CancellationToken cancellationToken = default);
}

internal sealed class RemediationInteractionService(
    IApplicationDataService dataService,
    ReleaseWorkflowService workflowService,
    TimeProvider timeProvider) : IRemediationInteractionService
{
    public async Task<ActiveRemediationInteraction?> GetActiveAsync(
        ReleaseId releaseId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(releaseId);
        var detail = await dataService.GetReleaseDetailAsync(releaseId, cancellationToken);
        return detail is null ? null : ToActive(detail);
    }

    public async Task<bool> SubmitAsync(
        ReleaseId releaseId,
        string correlationToken,
        IReadOnlyCollection<EvidenceRecord> evidenceReplacements,
        IReadOnlyCollection<ReadinessCheck> explicitlySelectedChecks,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(releaseId);
        correlationToken = DomainGuard.Required(correlationToken, nameof(correlationToken));
        ArgumentNullException.ThrowIfNull(evidenceReplacements);
        ArgumentNullException.ThrowIfNull(explicitlySelectedChecks);
        if (evidenceReplacements.Any(item => item.ReleaseId != releaseId)
            || evidenceReplacements.Select(item => item.Kind).Distinct().Count() != evidenceReplacements.Count)
        {
            throw new ArgumentException(
                "Evidence replacements must contain at most one record per branch for this release.",
                nameof(evidenceReplacements));
        }

        var detail = await dataService.GetReleaseDetailAsync(releaseId, cancellationToken);
        if (detail is null)
        {
            return false;
        }

        var active = ToActive(detail);
        if (active is null)
        {
            return false;
        }

        if (!string.Equals(active.CorrelationToken, correlationToken, StringComparison.Ordinal))
        {
            return false;
        }

        var submission = new RemediationSubmission(
            Guid.NewGuid(),
            active.Request.Id,
            new UtcInstant(timeProvider.GetUtcNow().ToUniversalTime()),
            evidenceReplacements.ToDictionary(item => item.Kind, item => item.Id),
            explicitlySelectedChecks);
        try
        {
            await workflowService.ResumeRemediationAsync(
                releaseId,
                new RemediationWorkflowResponse(submission, evidenceReplacements),
                cancellationToken);
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is InvalidOperationException)
        {
            throw new WorkflowInteractionException(exception);
        }
    }

    private static ActiveRemediationInteraction? ToActive(ReleaseDetailProjection detail)
    {
        if (WorkflowWaitResolver.Resolve(detail) is not PendingRemediationWait
            {
                RemediationRequestId: { } requestId,
            } pendingWait)
        {
            return null;
        }

        var activeRequest = detail.RemediationRequests.Single(request => request.Id == requestId);
        return new ActiveRemediationInteraction(
            detail.Release,
            activeRequest,
            detail.CurrentEvidence,
            pendingWait.WorkflowRequestId);
    }
}

internal sealed class RemediationHandler(
    ReleaseId releaseId,
    IApplicationDataService dataService)
{
    private readonly ReleaseId _releaseId =
        releaseId ?? throw new ArgumentNullException(nameof(releaseId));
    private readonly IApplicationDataService _dataService =
        dataService ?? throw new ArgumentNullException(nameof(dataService));

    public async Task<EvaluationRoundStart> HandleAsync(
        RemediationWorkflowResponse response,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        var detail = await _dataService.GetReleaseDetailAsync(_releaseId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Release '{_releaseId}' does not exist.");
        var request = detail.RemediationRequests.SingleOrDefault(
            item => item.Id == response.Submission.RequestId)
            ?? throw new InvalidOperationException(
                $"Remediation request '{response.Submission.RequestId}' does not belong to this release.");
        var nextSequence = detail.Timeline.IsEmpty
            ? 1
            : detail.Timeline[^1].Sequence + 1;

        var persisted = await _dataService.SaveRemediationSubmissionAsync(
            _releaseId,
            response.Submission,
            response.EvidenceReplacements,
            new TimelineEntry(
                Guid.NewGuid(),
                _releaseId,
                nextSequence,
                TimelineEntryKind.RemediationSubmitted,
                Explain(response.Submission),
                response.Submission.SubmittedAt),
            RoundAggregator.OperationKey(
                _releaseId,
                $"remediation:{response.Submission.Id:N}"),
            cancellationToken);

        return new EvaluationRoundStart(
            Guid.NewGuid(),
            request.RoundNumber + 1,
            persisted.SubmittedAt,
            persisted.ExplicitlySelectedChecks);
    }

    private static string Explain(RemediationSubmission submission)
    {
        var updates = submission.EvidenceUpdates.IsEmpty
            ? "no evidence replacements"
            : $"evidence replacements for {string.Join(", ", submission.EvidenceUpdates.Keys)}";
        var selections = submission.ExplicitlySelectedChecks.IsEmpty
            ? "no explicit rerun selections"
            : $"explicit rerun selections for {string.Join(", ", submission.ExplicitlySelectedChecks)}";
        return $"Remediation submitted with {updates} and {selections}.";
    }
}
