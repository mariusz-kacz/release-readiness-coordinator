using System.Collections.Immutable;
using ReleaseReadinessCoordinator.Data;
using ReleaseReadinessCoordinator.Domain;

namespace ReleaseReadinessCoordinator.Workflow;

public sealed record ActiveRemediationInteraction
{
    public ActiveRemediationInteraction(
        Release release,
        RemediationRequest request,
        IReadOnlyDictionary<EvidenceKind, EvidenceRecord> currentEvidence,
        string correlationToken)
    {
        ArgumentNullException.ThrowIfNull(release);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(currentEvidence);
        if (release.Phase is not ProcessPhase.WaitingForRemediation
            || request.ReleaseId != release.Submission.ReleaseId
            || currentEvidence.Values.Any(item => item.ReleaseId != release.Submission.ReleaseId))
        {
            throw new ArgumentException(
                "An active remediation interaction must describe one waiting release.");
        }

        Release = release;
        Request = request;
        CurrentEvidence = currentEvidence.ToImmutableDictionary();
        CorrelationToken = DomainGuard.Required(correlationToken, nameof(correlationToken));
    }

    public Release Release { get; }

    public RemediationRequest Request { get; }

    public ImmutableDictionary<EvidenceKind, EvidenceRecord> CurrentEvidence { get; }

    public string CorrelationToken { get; }
}

public enum RemediationSubmitOutcome
{
    Succeeded = 1,
    ReleaseNotFound = 2,
    NoLongerActive = 3,
    CorrelationMismatch = 4,
    TechnicalFailure = 5,
}

public interface IRemediationInteractionService
{
    Task<ActiveRemediationInteraction?> GetActiveAsync(
        ReleaseId releaseId,
        CancellationToken cancellationToken = default);

    Task<RemediationSubmitOutcome> SubmitAsync(
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

    public async Task<RemediationSubmitOutcome> SubmitAsync(
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
            return RemediationSubmitOutcome.ReleaseNotFound;
        }

        var active = ToActive(detail);
        if (active is null)
        {
            return RemediationSubmitOutcome.NoLongerActive;
        }

        if (!string.Equals(active.CorrelationToken, correlationToken, StringComparison.Ordinal))
        {
            return RemediationSubmitOutcome.CorrelationMismatch;
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
            return RemediationSubmitOutcome.Succeeded;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception) when (exception is WorkflowContinuationException
            or ApplicationDataConflictException
            or InvalidOperationException)
        {
            return RemediationSubmitOutcome.TechnicalFailure;
        }
    }

    private static ActiveRemediationInteraction? ToActive(ReleaseDetailProjection detail)
    {
        if (detail.Release.Phase is not ProcessPhase.WaitingForRemediation
            || detail.WorkflowCorrelation is not
            {
                PendingRequestKind: WorkflowRequestKind.Remediation,
            } correlation)
        {
            return null;
        }

        var answeredRequestIds = detail.RemediationSubmissions
            .Select(submission => submission.RequestId)
            .ToHashSet();
        var activeRequest = detail.RemediationRequests.SingleOrDefault(
            request => !answeredRequestIds.Contains(request.Id));
        return activeRequest is null
            ? null
            : new ActiveRemediationInteraction(
                detail.Release,
                activeRequest,
                detail.CurrentEvidence,
                correlation.PendingWorkflowRequestId);
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
