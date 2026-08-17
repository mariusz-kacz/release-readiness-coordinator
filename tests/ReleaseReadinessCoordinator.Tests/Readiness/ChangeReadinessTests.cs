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
        Assert.Equal(ChangeReadinessPolicy.PolicyVersion, policy.Version);
    }

    [Fact]
    public void Evidence_for_a_different_revision_remains_a_technical_failure()
    {
        var evidence = new ChangeEvidenceRecord(
            Guid.NewGuid(),
            new ReleaseRevisionKey("another-release", 1),
            1,
            Utc(2026, 8, 17, 8),
            null,
            isApproved: true,
            new UtcInterval(ApprovedStart, ApprovedEnd));

        Assert.Throws<InvalidOperationException>(() => Policy().Evaluate(Submission(), evidence));
    }

    private static ChangeReadinessPolicy Policy() => new();

    private static ReleaseSubmission Submission(UtcInterval? requestedWindow = null) => new(
        RevisionKey(),
        "orders",
        "2.4.0",
        requestedWindow ?? new UtcInterval(Utc(2026, 8, 17, 10), Utc(2026, 8, 17, 11)),
        Utc(2026, 8, 17, 7));

    private static ChangeEvidenceRecord Evidence(bool? isApproved, UtcInterval? approvedWindow) => new(
        Guid.NewGuid(),
        RevisionKey(),
        1,
        Utc(2026, 8, 17, 8),
        null,
        isApproved,
        approvedWindow);

    private static ReleaseRevisionKey RevisionKey() => new("release-42", 3);

    private static UtcInstant Utc(int year, int month, int day, int hour) =>
        new(new DateTimeOffset(year, month, day, hour, 0, 0, TimeSpan.Zero));
}

public sealed class ChangeReadinessBranchExecutionTests
{
    private static readonly UtcInstant ApprovedStart = Utc(2026, 8, 17, 9);
    private static readonly UtcInstant ApprovedEnd = Utc(2026, 8, 17, 12);

    [Fact]
    public async Task Simulated_provider_returns_configured_evidence_after_known_transient_failures()
    {
        var evidence = Evidence();
        var provider = new SimulatedChangeEvidenceProvider(
            evidence,
            knownTransientFailuresBeforeSuccess: 1);

        var exception = await Assert.ThrowsAsync<KnownTransientEvidenceProviderException>(
            async () => await provider.GetCurrentAsync(RevisionKey(), CancellationToken.None));
        var current = await provider.GetCurrentAsync(RevisionKey(), CancellationToken.None);

        Assert.Equal(evidence.Id, exception.EvidenceId);
        Assert.Same(evidence, current);
    }

    [Fact]
    public async Task Missing_evidence_does_not_retry_or_call_the_policy()
    {
        var provider = new CountingProvider(evidence: null);
        var policy = new CountingPolicy(PassingEvaluation());

        var result = await Executor(provider, policy).ExecuteAsync(Submission(), WorkItem());

        Assert.Equal(BranchOutcome.MissingEvidence, result.Outcome);
        Assert.Null(result.EvidenceId);
        Assert.Null(result.ValidUntil);
        Assert.Equal(1, provider.CallCount);
        Assert.Equal(0, policy.CallCount);
    }

    [Fact]
    public async Task Deterministic_blocker_does_not_retry()
    {
        var evidence = Evidence();
        var provider = new CountingProvider(evidence);
        var policy = new CountingPolicy(new ChangePolicyEvaluation(
            BranchOutcome.Blocked,
            validUntil: null,
            new Dictionary<string, string>
            {
                ["approval"] = "The Change is not approved.",
            }));

        var result = await Executor(provider, policy).ExecuteAsync(Submission(), WorkItem());

        Assert.Equal(BranchOutcome.Blocked, result.Outcome);
        Assert.Equal(evidence.Id, result.EvidenceId);
        Assert.Null(result.ValidUntil);
        Assert.Equal(1, provider.CallCount);
        Assert.Equal(1, policy.CallCount);
    }

    [Fact]
    public async Task Exhausted_known_transient_failures_return_attempt_details()
    {
        var evidence = Evidence();
        var provider = new CountingProvider(evidence, transientFailures: 3);
        var policy = new CountingPolicy(PassingEvaluation());

        var result = await Executor(provider, policy).ExecuteAsync(Submission(), WorkItem());

        Assert.Equal(BranchOutcome.TransientFailure, result.Outcome);
        Assert.Equal(evidence.Id, result.EvidenceId);
        Assert.Null(result.ValidUntil);
        Assert.Equal(3, provider.CallCount);
        Assert.Equal(0, policy.CallCount);
        Assert.Equal(3, result.Attempts.Length);
        Assert.All(result.Attempts, detail => Assert.Contains("known transient failure", detail));
    }

    [Fact]
    public async Task Unclassified_provider_failures_remain_technical_failures()
    {
        var provider = new CountingProvider(Evidence(), unclassifiedFailure: true);
        var policy = new CountingPolicy(PassingEvaluation());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Executor(provider, policy).ExecuteAsync(Submission(), WorkItem()));

        Assert.Equal(1, provider.CallCount);
        Assert.Equal(0, policy.CallCount);
    }

    [Fact]
    public async Task Policy_failures_remain_technical_failures()
    {
        var provider = new CountingProvider(Evidence());
        var policy = new ThrowingPolicy();

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => Executor(provider, policy).ExecuteAsync(Submission(), WorkItem()));

        Assert.Equal(1, provider.CallCount);
        Assert.Equal(1, policy.CallCount);
    }

    private static ChangeReadinessBranchExecutor Executor(
        IChangeEvidenceProvider provider,
        IChangeReadinessPolicy policy) => new(provider, policy);

    private static ChangePolicyEvaluation PassingEvaluation() => new(
        BranchOutcome.Passed,
        ApprovedEnd,
        new Dictionary<string, string>
        {
            ["ready"] = "Change evidence satisfies the policy.",
        });

    private static ReleaseReadinessCoordinator.Domain.BranchWorkItem WorkItem() => new(
        RevisionKey(),
        roundNumber: 1,
        ReadinessCheck.Change,
        WorkDisposition.Execute,
        PlanningReason.InitialEvaluation,
        "Executed because this is the initial evaluation.");

    private static ReleaseSubmission Submission() => new(
        RevisionKey(),
        "orders",
        "2.4.0",
        new UtcInterval(Utc(2026, 8, 17, 10), Utc(2026, 8, 17, 11)),
        Utc(2026, 8, 17, 7));

    private static ChangeEvidenceRecord Evidence() => new(
        Guid.NewGuid(),
        RevisionKey(),
        1,
        Utc(2026, 8, 17, 8),
        null,
        isApproved: true,
        new UtcInterval(ApprovedStart, ApprovedEnd));

    private static ReleaseRevisionKey RevisionKey() => new("release-42", 3);

    private static UtcInstant Utc(int year, int month, int day, int hour) =>
        new(new DateTimeOffset(year, month, day, hour, 0, 0, TimeSpan.Zero));

    private sealed class CountingProvider(
        ChangeEvidenceRecord? evidence,
        int transientFailures = 0,
        bool unclassifiedFailure = false) : IChangeEvidenceProvider
    {
        public int CallCount { get; private set; }

        public ValueTask<ChangeEvidenceRecord?> GetCurrentAsync(
            ReleaseRevisionKey releaseRevision,
            CancellationToken cancellationToken)
        {
            CallCount++;
            if (unclassifiedFailure)
            {
                throw new InvalidOperationException("Unclassified provider failure.");
            }

            if (CallCount <= transientFailures)
            {
                throw new KnownTransientEvidenceProviderException(
                    evidence?.Id ?? Guid.NewGuid(),
                    "The simulated Change source is temporarily unavailable.");
            }

            return ValueTask.FromResult(evidence);
        }
    }

    private sealed class CountingPolicy(ChangePolicyEvaluation evaluation) : IChangeReadinessPolicy
    {
        public int CallCount { get; private set; }

        public string Version => ChangeReadinessPolicy.PolicyVersion;

        public ChangePolicyEvaluation Evaluate(
            ReleaseSubmission submission,
            ChangeEvidenceRecord evidence)
        {
            CallCount++;
            return evaluation;
        }
    }

    private sealed class ThrowingPolicy : IChangeReadinessPolicy
    {
        public int CallCount { get; private set; }

        public string Version => ChangeReadinessPolicy.PolicyVersion;

        public ChangePolicyEvaluation Evaluate(
            ReleaseSubmission submission,
            ChangeEvidenceRecord evidence)
        {
            CallCount++;
            throw new InvalidOperationException("Policy invariant failure.");
        }
    }
}

public sealed class ChangeReadinessWorkflowIntegrationTests
{
    [Fact]
    public async Task Real_graph_change_branch_uses_provider_and_policy_instead_of_simulated_outcome()
    {
        var submission = Submission();
        var changeEvidence = ChangeEvidence(submission.Key);
        var changeProvider = new CountingProvider(changeEvidence);
        var changePolicy = new CountingPolicy();
        var workflow = ReadinessWorkflowTestFactory.CreateWithChange(
            submission,
            changeProvider,
            changePolicy);
        var input = new EvaluationRoundPlan(
            RoundNumber: 1,
            Test: new BranchPlan(BranchDisposition.Execute, BranchOutcome.Passed),
            Security: new BranchPlan(BranchDisposition.Execute, BranchOutcome.Passed),
            Change: new BranchPlan(BranchDisposition.Execute, BranchOutcome.Blocked));

        await using var run = await InProcessExecution.RunAsync(workflow, input);

        var output = Assert.Single(run.NewEvents.OfType<WorkflowOutputEvent>());
        var round = Assert.IsType<EvaluationRoundResult>(output.Data);
        var changeResult = Assert.Single(
            round.Results,
            result => result.Branch is ReadinessBranch.Change);
        Assert.Equal(BranchOutcome.Passed, changeResult.Outcome);
        var evaluation = Assert.IsType<ReleaseReadinessCoordinator.Domain.BranchResult>(
            changeResult.Evaluation);
        Assert.Equal(changeEvidence.Id, evaluation.EvidenceId);
        Assert.Equal(ChangeReadinessPolicy.PolicyVersion, evaluation.PolicyVersion);
        Assert.Equal(1, changeProvider.CallCount);
        Assert.Equal(1, changePolicy.CallCount);
    }

    private static ReleaseSubmission Submission() => new(
        new ReleaseRevisionKey("release-graph", 1),
        "orders",
        "2.4.0",
        new UtcInterval(Utc(2026, 8, 17, 10), Utc(2026, 8, 17, 11)),
        Utc(2026, 8, 17, 7));

    private static ChangeEvidenceRecord ChangeEvidence(ReleaseRevisionKey releaseRevision) => new(
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
            ReleaseRevisionKey releaseRevision,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return ValueTask.FromResult<ChangeEvidenceRecord?>(evidence);
        }
    }

    private sealed class CountingPolicy : IChangeReadinessPolicy
    {
        public int CallCount { get; private set; }

        public string Version => ChangeReadinessPolicy.PolicyVersion;

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
