using ReleaseReadinessCoordinator.Data;
using ReleaseReadinessCoordinator.Domain;

namespace ReleaseReadinessCoordinator.Workflow;

public sealed class ReleaseSubmissionApplicationService
{
    private readonly IApplicationDataService _dataService;
    private readonly ReleaseWorkflowService _workflowService;
    private readonly TimeProvider _timeProvider;

    internal ReleaseSubmissionApplicationService(
        IApplicationDataService dataService,
        ReleaseWorkflowService workflowService,
        TimeProvider timeProvider)
    {
        _dataService = dataService;
        _workflowService = workflowService;
        _timeProvider = timeProvider;
    }

    public async Task<Release> SubmitAsync(
        ReleaseSubmission submission,
        IReadOnlyCollection<EvidenceRecord> initialEvidence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        ArgumentNullException.ThrowIfNull(initialEvidence);

        var operationId = Guid.NewGuid().ToString("N");
        var timeline = new TimelineEntry(
            Guid.NewGuid(),
            submission.ReleaseId,
            1,
            TimelineEntryKind.ReleaseSubmitted,
            $"Release {submission.ReleaseId} submitted.",
            submission.SubmittedAt);
        var release = await _dataService.SubmitReleaseAsync(
            submission,
            initialEvidence,
            timeline,
            $"submission:{operationId}",
            cancellationToken);

        var sessionId = $"release-{Uri.EscapeDataString(submission.ReleaseId.Value)}";
        await _workflowService.StartAsync(
            submission,
            new EvaluationRoundStart(
                Guid.NewGuid(),
                1,
                submission.SubmittedAt,
                []),
            sessionId,
            cancellationToken);
        return release;
    }
}
