using ReleaseReadinessCoordinator.Data;
using ReleaseReadinessCoordinator.Domain;
using ReleaseReadinessCoordinator.Readiness;

namespace ReleaseReadinessCoordinator.Workflow;

public sealed class ReleaseSubmissionApplicationService
{
    private readonly IApplicationDataService _dataService;
    private readonly CheckpointStoreCoordinator _checkpointCoordinator;
    private readonly TimeProvider _timeProvider;

    internal ReleaseSubmissionApplicationService(
        IApplicationDataService dataService,
        CheckpointStoreCoordinator checkpointCoordinator,
        TimeProvider timeProvider)
    {
        _dataService = dataService;
        _checkpointCoordinator = checkpointCoordinator;
        _timeProvider = timeProvider;
    }

    public async Task<ReleaseRevision> SubmitAsync(
        ReleaseSubmission submission,
        IReadOnlyCollection<EvidenceRecord> initialEvidence,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        ArgumentNullException.ThrowIfNull(initialEvidence);

        var operationId = Guid.NewGuid().ToString("N");
        var timeline = new TimelineEntry(
            Guid.NewGuid(),
            submission.Key,
            1,
            TimelineEntryKind.ReleaseSubmitted,
            $"Release {submission.Key.ReleaseId} revision {submission.Key.Revision} submitted.",
            submission.SubmittedAt);
        var release = await _dataService.SubmitReleaseAsync(
            submission,
            initialEvidence,
            timeline,
            $"submission:{operationId}",
            cancellationToken);

        var sessionId = $"release-{Uri.EscapeDataString(submission.Key.ReleaseId)}-revision-{submission.Key.Revision}";
        var initialBranchPlan = new BranchPlan(
            BranchDisposition.Execute,
            BranchOutcome.Blocked);
        var started = await _checkpointCoordinator.StartAsync(
            ReleaseWorkflowFactory.Create(
                submission,
                new SimulatedTestEvidenceProvider(
                    initialEvidence.OfType<TestEvidenceRecord>().SingleOrDefault()),
                new TestReadinessPolicy(_timeProvider),
                new SimulatedSecurityEvidenceProvider(
                    initialEvidence.OfType<SecurityEvidenceRecord>().SingleOrDefault()),
                new SecurityReadinessPolicy(_timeProvider)),
            new EvaluationRoundPlan(
                1,
                initialBranchPlan,
                initialBranchPlan,
                initialBranchPlan),
            sessionId,
            cancellationToken);
        var correlatedAt = new UtcInstant(_timeProvider.GetUtcNow());
        var correlation = new WorkflowCorrelationRecord(
            submission.Key,
            sessionId,
            started.PendingRequest.RequestId,
            started.PendingRequest.Kind switch
            {
                ExternalWaitKind.Remediation => WorkflowRequestKind.Remediation,
                ExternalWaitKind.Approval => WorkflowRequestKind.Approval,
                _ => throw new InvalidOperationException(
                    $"Unknown workflow request kind '{started.PendingRequest.Kind}'."),
            },
            correlatedAt);
        await _dataService.SaveWorkflowCorrelationAsync(
            correlation,
            $"submission:{operationId}:workflow-correlation",
            cancellationToken);
        return release;
    }
}
