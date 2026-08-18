using ReleaseReadinessCoordinator.Data;
using ReleaseReadinessCoordinator.Domain;

namespace ReleaseReadinessCoordinator.Workflow;

internal sealed class RemediationHandler(
    ReleaseRevisionKey releaseRevision,
    IApplicationDataService dataService)
{
    private readonly ReleaseRevisionKey _releaseRevision =
        releaseRevision ?? throw new ArgumentNullException(nameof(releaseRevision));
    private readonly IApplicationDataService _dataService =
        dataService ?? throw new ArgumentNullException(nameof(dataService));

    public async Task<EvaluationRoundStart> HandleAsync(
        RemediationWorkflowResponse response,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        var detail = await _dataService.GetReleaseDetailAsync(_releaseRevision, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Release revision '{_releaseRevision.ReleaseId}/{_releaseRevision.Revision}' does not exist.");
        var request = detail.RemediationRequests.SingleOrDefault(
            item => item.Id == response.Submission.RequestId)
            ?? throw new InvalidOperationException(
                $"Remediation request '{response.Submission.RequestId}' does not belong to this release revision.");
        var nextSequence = detail.Timeline.IsEmpty
            ? 1
            : detail.Timeline[^1].Sequence + 1;

        var persisted = await _dataService.SaveRemediationSubmissionAsync(
            _releaseRevision,
            response.Submission,
            response.EvidenceReplacements,
            new TimelineEntry(
                Guid.NewGuid(),
                _releaseRevision,
                nextSequence,
                TimelineEntryKind.RemediationSubmitted,
                Explain(response.Submission),
                response.Submission.SubmittedAt),
            RoundAggregator.OperationKey(
                _releaseRevision,
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
