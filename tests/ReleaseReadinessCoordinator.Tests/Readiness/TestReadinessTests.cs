using Microsoft.Agents.AI.Workflows;
using ReleaseReadinessCoordinator.Domain;
using ReleaseReadinessCoordinator.Readiness;
using ReleaseReadinessCoordinator.Workflow;

namespace ReleaseReadinessCoordinator.Tests.Readiness;

public sealed class TestReadinessPolicyTests
{
    private static readonly UtcInstant CompletedAt = Utc(2026, 8, 17, 8);

    [Fact]
    public void Required_test_facts_must_all_be_present()
    {
        var policy = Policy();
        TestEvidenceRecord[] evidenceWithMissingFacts =
        [
            Evidence(testRunVersion: null),
            new TestEvidenceRecord(
                Guid.NewGuid(), Id(), 1, Utc(2026, 8, 17, 9), null,
                "2.4.0", completedAt: null, 0.98m, []),
            Evidence(passRate: null),
            new TestEvidenceRecord(
                Guid.NewGuid(), Id(), 1, Utc(2026, 8, 17, 9), null,
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
        var policy = Policy();

        var evaluation = policy.Evaluate(
            Submission(releaseVersion: "2.4.0"),
            Evidence(testRunVersion: "2.4.0", passRate: 0.95m, criticalSuiteFailures: []));

        Assert.Equal(BranchOutcome.Passed, evaluation.Outcome);
    }

    [Theory]
    [InlineData("2.4.0-rc.1", 0.95, false)]
    [InlineData("2.4.0", 0.9499, false)]
    [InlineData("2.4.0", 0.95, true)]
    public void Deterministic_policy_misses_block(
        string testRunVersion,
        decimal passRate,
        bool hasCriticalFailure)
    {
        var policy = Policy();

        var evaluation = policy.Evaluate(
            Submission(),
            Evidence(
                testRunVersion: testRunVersion,
                passRate: passRate,
                criticalSuiteFailures: hasCriticalFailure ? ["payments-critical"] : []));

        Assert.Equal(BranchOutcome.Blocked, evaluation.Outcome);
    }

    private static TestReadinessPolicy Policy() => new();

    private static ReleaseSubmission Submission(string releaseVersion = "2.4.0") => new(
        Id(),
        "orders",
        releaseVersion,
        new UtcInterval(Utc(2026, 8, 20, 20), Utc(2026, 8, 20, 21)),
        Utc(2026, 8, 17, 7));

    private static TestEvidenceRecord Evidence(
        string? testRunVersion = "2.4.0",
        decimal? passRate = 0.98m,
        IEnumerable<string>? criticalSuiteFailures = default) => new(
            Guid.NewGuid(),
            Id(),
            1,
            Utc(2026, 8, 17, 9),
            null,
            testRunVersion,
            CompletedAt,
            passRate,
            criticalSuiteFailures ?? []);

    private static ReleaseId Id() => new("release-42");

    private static UtcInstant Utc(int year, int month, int day, int hour) =>
        new(new DateTimeOffset(year, month, day, hour, 0, 0, TimeSpan.Zero));

}
public sealed class TestReadinessWorkflowIntegrationTests
{
    [Fact]
    public async Task Real_graph_test_branch_uses_provider_and_policy_instead_of_simulated_outcome()
    {
        var submission = Submission();
        var evidence = Evidence(submission.ReleaseId);
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
        var detail = await host.DataService.GetReleaseDetailAsync(host.Submission.ReleaseId);
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
        new ReleaseId("release-graph"),
        "orders",
        "2.4.0",
        new UtcInterval(Utc(2026, 8, 20, 20), Utc(2026, 8, 20, 21)),
        Utc(2026, 8, 17, 7));

    private static TestEvidenceRecord Evidence(ReleaseId releaseRevision) => new(
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
            ReleaseId releaseRevision,
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
                new Dictionary<string, string>
                {
                    ["ready"] = "Test evidence satisfies the policy.",
                });
        }
    }
}
