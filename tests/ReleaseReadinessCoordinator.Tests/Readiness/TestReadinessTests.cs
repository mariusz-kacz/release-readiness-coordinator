using Microsoft.Agents.AI.Workflows;
using ReleaseReadinessCoordinator.Domain;
using ReleaseReadinessCoordinator.Readiness;
using ReleaseReadinessCoordinator.Workflow;

namespace ReleaseReadinessCoordinator.Tests.Readiness;

public sealed class TestReadinessPolicyTests
{
    private static readonly UtcInstant CompletedAt = Utc(2026, 8, 17, 8);
    private static readonly UtcInstant Deadline = Utc(2026, 8, 18, 8);

    [Fact]
    public void Readiness_policy_contracts_expose_only_evaluation_behavior()
    {
        Assert.Empty(typeof(ITestReadinessPolicy).GetProperties());
        Assert.Empty(typeof(ISecurityReadinessPolicy).GetProperties());
        Assert.Empty(typeof(IChangeReadinessPolicy).GetProperties());
    }

    [Fact]
    public void Required_test_facts_must_all_be_present()
    {
        var policy = PolicyAt(CompletedAt.Value.AddHours(1));
        TestEvidenceRecord[] evidenceWithMissingFacts =
        [
            Evidence(testRunVersion: null),
            new TestEvidenceRecord(
                Guid.NewGuid(), RevisionKey(), 1, Utc(2026, 8, 17, 9), null,
                "2.4.0", completedAt: null, 0.98m, []),
            Evidence(passRate: null),
            new TestEvidenceRecord(
                Guid.NewGuid(), RevisionKey(), 1, Utc(2026, 8, 17, 9), null,
                "2.4.0", CompletedAt, 0.98m, criticalSuiteFailures: null),
        ];

        Assert.All(
            evidenceWithMissingFacts,
            evidence => Assert.Equal(
                BranchOutcome.MissingEvidence,
                policy.Evaluate(Submission(), evidence).Outcome));
    }

    [Fact]
    public void Exact_version_95_percent_and_no_critical_failures_pass()
    {
        var policy = PolicyAt(Deadline.Value.AddTicks(-1));

        var evaluation = policy.Evaluate(
            Submission(releaseVersion: "2.4.0"),
            Evidence(testRunVersion: "2.4.0", passRate: 0.95m, criticalSuiteFailures: []));

        Assert.Equal(BranchOutcome.Passed, evaluation.Outcome);
        Assert.Equal(Deadline, evaluation.ValidUntil);
    }

    [Theory]
    [InlineData("2.4.0-rc.1", 0.95, false, -1)]
    [InlineData("2.4.0", 0.9499, false, -1)]
    [InlineData("2.4.0", 0.95, true, -1)]
    [InlineData("2.4.0", 0.95, false, 0)]
    public void Deterministic_policy_misses_block(
        string testRunVersion,
        decimal passRate,
        bool hasCriticalFailure,
        long ticksFromDeadline)
    {
        var policy = PolicyAt(Deadline.Value.AddTicks(ticksFromDeadline));

        var evaluation = policy.Evaluate(
            Submission(),
            Evidence(
                testRunVersion: testRunVersion,
                passRate: passRate,
                criticalSuiteFailures: hasCriticalFailure ? ["payments-critical"] : []));

        Assert.Equal(BranchOutcome.Blocked, evaluation.Outcome);
        Assert.Null(evaluation.ValidUntil);
    }

    private static TestReadinessPolicy PolicyAt(DateTimeOffset now) =>
        new(new FixedTimeProvider(now));

    private static ReleaseSubmission Submission(string releaseVersion = "2.4.0") => new(
        RevisionKey(),
        "orders",
        releaseVersion,
        new UtcInterval(Utc(2026, 8, 20, 20), Utc(2026, 8, 20, 21)),
        Utc(2026, 8, 17, 7));

    private static TestEvidenceRecord Evidence(
        string? testRunVersion = "2.4.0",
        decimal? passRate = 0.98m,
        IEnumerable<string>? criticalSuiteFailures = default) => new(
            Guid.NewGuid(),
            RevisionKey(),
            1,
            Utc(2026, 8, 17, 9),
            null,
            testRunVersion,
            CompletedAt,
            passRate,
            criticalSuiteFailures ?? []);

    private static ReleaseRevisionKey RevisionKey() => new("release-42", 3);

    private static UtcInstant Utc(int year, int month, int day, int hour) =>
        new(new DateTimeOffset(year, month, day, hour, 0, 0, TimeSpan.Zero));

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

public sealed class TestReadinessBranchExecutionTests
{
    private static readonly UtcInstant CompletedAt = Utc(2026, 8, 17, 8);

    [Fact]
    public async Task Simulated_provider_returns_configured_evidence_after_known_transient_failures()
    {
        var evidence = Evidence();
        var provider = new SimulatedTestEvidenceProvider(evidence, knownTransientFailuresBeforeSuccess: 1);

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
        var executor = Executor(provider, policy);

        var result = await executor.ExecuteAsync(Submission(), WorkItem());

        Assert.Equal(BranchOutcome.MissingEvidence, result.Outcome);
        Assert.Null(result.EvidenceId);
        Assert.Equal(1, provider.CallCount);
        Assert.Equal(0, policy.CallCount);
    }

    [Fact]
    public async Task Deterministic_blocker_does_not_retry()
    {
        var evidence = Evidence();
        var provider = new CountingProvider(evidence);
        var policy = new CountingPolicy(new TestPolicyEvaluation(
            BranchOutcome.Blocked,
            validUntil: null,
            new Dictionary<string, string> { ["pass-rate"] = "The pass rate is below 95%." }));
        var executor = Executor(provider, policy);

        var result = await executor.ExecuteAsync(Submission(), WorkItem());

        Assert.Equal(BranchOutcome.Blocked, result.Outcome);
        Assert.Equal(1, provider.CallCount);
        Assert.Equal(1, policy.CallCount);
    }

    [Fact]
    public async Task Known_transient_failures_retry_twice_then_succeed()
    {
        var evidence = Evidence();
        var provider = new CountingProvider(evidence, transientFailures: 2);
        var policy = new CountingPolicy(PassingEvaluation());
        var executor = Executor(provider, policy);

        var result = await executor.ExecuteAsync(Submission(), WorkItem());

        Assert.Equal(BranchOutcome.Passed, result.Outcome);
        Assert.Equal(3, provider.CallCount);
        Assert.Equal(1, policy.CallCount);
        Assert.Equal(3, result.Attempts.Length);
    }

    [Fact]
    public async Task Exhausted_known_transient_failures_return_attempt_details()
    {
        var evidence = Evidence();
        var provider = new CountingProvider(evidence, transientFailures: 3);
        var policy = new CountingPolicy(PassingEvaluation());
        var executor = Executor(provider, policy);

        var result = await executor.ExecuteAsync(Submission(), WorkItem());

        Assert.Equal(BranchOutcome.TransientFailure, result.Outcome);
        Assert.Equal(evidence.Id, result.EvidenceId);
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
        var executor = Executor(provider, policy);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => executor.ExecuteAsync(Submission(), WorkItem()));

        Assert.Equal(1, provider.CallCount);
        Assert.Equal(0, policy.CallCount);
    }

    private static TestReadinessBranchExecutor Executor(
        ITestEvidenceProvider provider,
        ITestReadinessPolicy policy) => new(provider, policy);

    private static TestPolicyEvaluation PassingEvaluation() => new(
        BranchOutcome.Passed,
        Utc(2026, 8, 18, 8),
        new Dictionary<string, string> { ["ready"] = "Test evidence satisfies the policy." });

    private static ReleaseReadinessCoordinator.Domain.BranchWorkItem WorkItem() => new(
        RevisionKey(),
        roundNumber: 1,
        ReadinessCheck.Test,
        WorkDisposition.Execute,
        PlanningReason.InitialEvaluation,
        "Executed because this is the initial evaluation.");

    private static ReleaseSubmission Submission() => new(
        RevisionKey(),
        "orders",
        "2.4.0",
        new UtcInterval(Utc(2026, 8, 20, 20), Utc(2026, 8, 20, 21)),
        Utc(2026, 8, 17, 7));

    private static TestEvidenceRecord Evidence() => new(
        Guid.NewGuid(),
        RevisionKey(),
        1,
        Utc(2026, 8, 17, 9),
        null,
        "2.4.0",
        CompletedAt,
        0.98m,
        []);

    private static ReleaseRevisionKey RevisionKey() => new("release-42", 3);

    private static UtcInstant Utc(int year, int month, int day, int hour) =>
        new(new DateTimeOffset(year, month, day, hour, 0, 0, TimeSpan.Zero));

    private sealed class CountingProvider(
        TestEvidenceRecord? evidence,
        int transientFailures = 0,
        bool unclassifiedFailure = false) : ITestEvidenceProvider
    {
        public int CallCount { get; private set; }

        public ValueTask<TestEvidenceRecord?> GetCurrentAsync(
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
                    "The simulated Test source is temporarily unavailable.");
            }

            return ValueTask.FromResult(evidence);
        }
    }

    private sealed class CountingPolicy(TestPolicyEvaluation evaluation) : ITestReadinessPolicy
    {
        public int CallCount { get; private set; }

        public TestPolicyEvaluation Evaluate(
            ReleaseSubmission submission,
            TestEvidenceRecord evidence)
        {
            CallCount++;
            return evaluation;
        }
    }
}

public sealed class TestReadinessWorkflowIntegrationTests
{
    [Fact]
    public async Task Real_graph_test_branch_uses_provider_and_policy_instead_of_simulated_outcome()
    {
        var submission = Submission();
        var evidence = Evidence(submission.Key);
        var provider = new CountingProvider(evidence);
        var policy = new CountingPolicy();
        await using var host = await ReadinessWorkflowTestHost.CreateWithTestAsync(
            submission,
            evidence,
            provider,
            policy);

        await using var run = await InProcessExecution.RunAsync(
            host.CreateWorkflow(),
            host.Input);

        Assert.DoesNotContain(
            run.NewEvents.OfType<WorkflowOutputEvent>(),
            output => output.Data is EvaluationRound);
        var detail = await host.DataService.GetReleaseDetailAsync(host.Submission.Key);
        var round = Assert.Single(detail!.EvaluationRounds);
        var testResult = Assert.Single(
            round.Results,
            result => result.Check is ReadinessCheck.Test);
        Assert.Equal(BranchOutcome.Passed, testResult.Outcome);
        Assert.Equal(evidence.Id, testResult.EvidenceId);
        Assert.Equal(1, provider.CallCount);
        Assert.Equal(1, policy.CallCount);
    }

    private static ReleaseSubmission Submission() => new(
        new ReleaseRevisionKey("release-graph", 1),
        "orders",
        "2.4.0",
        new UtcInterval(Utc(2026, 8, 20, 20), Utc(2026, 8, 20, 21)),
        Utc(2026, 8, 17, 7));

    private static TestEvidenceRecord Evidence(ReleaseRevisionKey releaseRevision) => new(
        Guid.NewGuid(),
        releaseRevision,
        1,
        Utc(2026, 8, 17, 9),
        null,
        "2.4.0",
        Utc(2026, 8, 17, 8),
        0.98m,
        []);

    private static UtcInstant Utc(int year, int month, int day, int hour) =>
        new(new DateTimeOffset(year, month, day, hour, 0, 0, TimeSpan.Zero));

    private sealed class CountingProvider(TestEvidenceRecord evidence) : ITestEvidenceProvider
    {
        public int CallCount { get; private set; }

        public ValueTask<TestEvidenceRecord?> GetCurrentAsync(
            ReleaseRevisionKey releaseRevision,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return ValueTask.FromResult<TestEvidenceRecord?>(evidence);
        }
    }

    private sealed class CountingPolicy : ITestReadinessPolicy
    {
        public int CallCount { get; private set; }

        public TestPolicyEvaluation Evaluate(
            ReleaseSubmission submission,
            TestEvidenceRecord evidence)
        {
            CallCount++;
            return new TestPolicyEvaluation(
                BranchOutcome.Passed,
                Utc(2026, 8, 18, 8),
                new Dictionary<string, string>
                {
                    ["ready"] = "Test evidence satisfies the policy.",
                });
        }
    }
}
