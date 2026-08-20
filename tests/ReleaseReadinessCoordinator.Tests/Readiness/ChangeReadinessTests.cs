using Microsoft.Agents.AI.Workflows;
using ReleaseReadinessCoordinator.Domain;
using ReleaseReadinessCoordinator.Readiness;
using ReleaseReadinessCoordinator.Workflow;

namespace ReleaseReadinessCoordinator.Tests.Readiness;

public sealed class ChangeReadinessPolicyTests
{
    private static readonly UtcInstant ApprovedStart = Utc(2026, 8, 17, 9);
    private static readonly UtcInstant ApprovedEnd = Utc(2026, 8, 17, 12);

    [Theory]
    [InlineData(null, true)]
    [InlineData(true, false)]
    [InlineData(null, false)]
    public void Missing_approval_or_approved_window_maps_to_missing_evidence(
        bool? isApproved,
        bool includeApprovedWindow)
    {
        var evaluation = Policy().Evaluate(
            Submission(),
            Evidence(
                isApproved,
                includeApprovedWindow
                    ? new UtcInterval(ApprovedStart, ApprovedEnd)
                    : null));

        Assert.Equal(BranchOutcome.MissingEvidence, evaluation.Outcome);
        Assert.Null(evaluation.ValidUntil);
    }

    [Fact]
    public void Unapproved_change_maps_to_blocked_without_a_reuse_deadline()
    {
        var evaluation = Policy().Evaluate(
            Submission(),
            Evidence(isApproved: false, new UtcInterval(ApprovedStart, ApprovedEnd)));

        Assert.Equal(BranchOutcome.Blocked, evaluation.Outcome);
        Assert.Null(evaluation.ValidUntil);
    }

    [Theory]
    [InlineData(0, 0, BranchOutcome.Passed)]
    [InlineData(1, -1, BranchOutcome.Passed)]
    [InlineData(-1, 0, BranchOutcome.Blocked)]
    [InlineData(0, 1, BranchOutcome.Blocked)]
    public void Requested_window_must_be_fully_contained_with_inclusive_boundaries(
        long requestedStartTicksFromApprovedStart,
        long requestedEndTicksFromApprovedEnd,
        BranchOutcome expected)
    {
        var submission = Submission(
            new UtcInterval(
                new UtcInstant(ApprovedStart.Value.AddTicks(requestedStartTicksFromApprovedStart)),
                new UtcInstant(ApprovedEnd.Value.AddTicks(requestedEndTicksFromApprovedEnd))));

        var evaluation = Policy().Evaluate(
            submission,
            Evidence(isApproved: true, new UtcInterval(ApprovedStart, ApprovedEnd)));

        Assert.Equal(expected, evaluation.Outcome);
        Assert.Equal(
            expected is BranchOutcome.Passed ? ApprovedEnd : null,
            evaluation.ValidUntil);
    }

    [Fact]
    public void Passing_change_uses_the_approved_window_end_as_its_reuse_deadline()
    {
        var policy = Policy();

        var evaluation = policy.Evaluate(
            Submission(),
            Evidence(isApproved: true, new UtcInterval(ApprovedStart, ApprovedEnd)));

        Assert.Equal(BranchOutcome.Passed, evaluation.Outcome);
        Assert.Equal(ApprovedEnd, evaluation.ValidUntil);
    }

    [Fact]
    public void Evidence_for_a_different_revision_remains_a_technical_failure()
    {
        var evidence = new ChangeEvidenceRecord(
            Guid.NewGuid(),
            new ReleaseId("another-release"),
            1,
            Utc(2026, 8, 17, 8),
            null,
            isApproved: true,
            new UtcInterval(ApprovedStart, ApprovedEnd));

        Assert.Throws<InvalidOperationException>(() => Policy().Evaluate(Submission(), evidence));
    }

    private static ChangeReadinessPolicy Policy() => new();

    private static ReleaseSubmission Submission(UtcInterval? requestedWindow = null) => new(
        Id(),
        "orders",
        "2.4.0",
        requestedWindow ?? new UtcInterval(Utc(2026, 8, 17, 10), Utc(2026, 8, 17, 11)),
        Utc(2026, 8, 17, 7));

    private static ChangeEvidenceRecord Evidence(bool? isApproved, UtcInterval? approvedWindow) => new(
        Guid.NewGuid(),
        Id(),
        1,
        Utc(2026, 8, 17, 8),
        null,
        isApproved,
        approvedWindow);

    private static ReleaseId Id() => new("release-42");

    private static UtcInstant Utc(int year, int month, int day, int hour) =>
        new(new DateTimeOffset(year, month, day, hour, 0, 0, TimeSpan.Zero));
}

public sealed class ChangeReadinessWorkflowIntegrationTests
{
    [Fact]
    public async Task Real_graph_change_branch_uses_provider_and_policy_instead_of_simulated_outcome()
    {
        var submission = Submission();
        var changeEvidence = ChangeEvidence(submission.ReleaseId);
        var changeProvider = new CountingProvider(changeEvidence);
        var changePolicy = new CountingPolicy();
        await using var host = await ReadinessWorkflowTestHost.CreateWithChangeAsync(
            submission,
            changeEvidence,
            changeProvider,
            changePolicy);

        await using var run = await InProcessExecution.RunAsync(
            host.CreateWorkflow(),
            host.Input);

        Assert.DoesNotContain(
            run.NewEvents.OfType<WorkflowOutputEvent>(),
            output => output.Data is EvaluationRound);
        var detail = await host.DataService.GetReleaseDetailAsync(host.Submission.ReleaseId);
        var round = Assert.Single(detail!.EvaluationRounds);
        var changeResult = Assert.Single(
            round.Results,
            result => result.Check is ReadinessCheck.Change);
        Assert.Equal(BranchOutcome.Passed, changeResult.Outcome);
        Assert.Equal(changeEvidence.Id, changeResult.EvidenceId);
        Assert.Equal(1, changeProvider.CallCount);
        Assert.Equal(1, changePolicy.CallCount);
    }

    private static ReleaseSubmission Submission() => new(
        new ReleaseId("release-graph"),
        "orders",
        "2.4.0",
        new UtcInterval(Utc(2026, 8, 17, 10), Utc(2026, 8, 17, 11)),
        Utc(2026, 8, 17, 7));

    private static ChangeEvidenceRecord ChangeEvidence(ReleaseId releaseRevision) => new(
        Guid.NewGuid(),
        releaseRevision,
        1,
        Utc(2026, 8, 17, 9),
        null,
        isApproved: true,
        new UtcInterval(Utc(2026, 8, 17, 9), Utc(2026, 8, 17, 12)));

    private static UtcInstant Utc(int year, int month, int day, int hour) =>
        new(new DateTimeOffset(year, month, day, hour, 0, 0, TimeSpan.Zero));

    private sealed class CountingProvider(ChangeEvidenceRecord evidence) : IChangeEvidenceProvider
    {
        public int CallCount { get; private set; }

        public ValueTask<ChangeEvidenceRecord?> GetCurrentAsync(
            ReleaseId releaseRevision,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return ValueTask.FromResult<ChangeEvidenceRecord?>(evidence);
        }
    }

    private sealed class CountingPolicy : IChangeReadinessPolicy
    {
        public int CallCount { get; private set; }

        public ChangePolicyEvaluation Evaluate(
            ReleaseSubmission submission,
            ChangeEvidenceRecord evidence)
        {
            CallCount++;
            return new ChangePolicyEvaluation(
                BranchOutcome.Passed,
                Utc(2026, 8, 17, 12),
                new Dictionary<string, string>
                {
                    ["ready"] = "Change evidence satisfies the policy.",
                });
        }
    }
}
