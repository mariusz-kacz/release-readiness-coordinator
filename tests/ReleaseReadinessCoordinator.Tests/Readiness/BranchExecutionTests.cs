using System.Collections.Immutable;
using ReleaseReadinessCoordinator.Domain;
using ReleaseReadinessCoordinator.Readiness;

namespace ReleaseReadinessCoordinator.Tests.Readiness;

public sealed class BranchExecutionTests
{
    [Fact]
    public async Task Simulated_providers_return_configured_evidence_after_a_known_transient_failure()
    {
        var testEvidence = TestEvidence();
        var securityEvidence = SecurityEvidence();
        var changeEvidence = ChangeEvidence();

        await AssertTransientThenEvidence(
            new SimulatedTestEvidenceProvider(testEvidence, knownTransientFailuresBeforeSuccess: 1),
            testEvidence);
        await AssertTransientThenEvidence(
            new SimulatedSecurityEvidenceProvider(securityEvidence, knownTransientFailuresBeforeSuccess: 1),
            securityEvidence);
        await AssertTransientThenEvidence(
            new SimulatedChangeEvidenceProvider(changeEvidence, knownTransientFailuresBeforeSuccess: 1),
            changeEvidence);
    }

    [Fact]
    public async Task Missing_evidence_does_not_retry_or_evaluate_policy()
    {
        var provider = new CountingProvider(evidence: null);
        var evaluator = new CountingEvaluator(PassingEvaluation());

        var result = await ExecuteAsync(provider, evaluator);

        Assert.Equal(BranchOutcome.MissingEvidence, result.Outcome);
        Assert.Null(result.EvidenceId);
        Assert.Equal(1, provider.CallCount);
        Assert.Equal(0, evaluator.CallCount);
        Assert.Equal(
            "Check completed on the first attempt.",
            Assert.Single(result.Attempts));
    }

    [Fact]
    public async Task Deterministic_blocker_does_not_retry()
    {
        var provider = new CountingProvider(TestEvidence());
        var evaluator = new CountingEvaluator(new Evaluation(
            BranchOutcome.Blocked,
            new Dictionary<string, string>
            {
                ["pass-rate"] = "The pass rate is below 95%.",
            }));

        var result = await ExecuteAsync(provider, evaluator);

        Assert.Equal(BranchOutcome.Blocked, result.Outcome);
        Assert.Equal(1, provider.CallCount);
        Assert.Equal(1, evaluator.CallCount);
    }

    [Fact]
    public async Task Known_transient_failures_retry_twice_then_succeed()
    {
        var provider = new CountingProvider(TestEvidence(), transientFailures: 2);
        var evaluator = new CountingEvaluator(PassingEvaluation());

        var result = await ExecuteAsync(provider, evaluator);

        Assert.Equal(BranchOutcome.Passed, result.Outcome);
        Assert.Equal(3, provider.CallCount);
        Assert.Equal(1, evaluator.CallCount);
        Assert.Equal(3, result.Attempts.Length);
    }

    [Fact]
    public async Task Exhausted_known_transient_failures_return_attempt_details()
    {
        var evidence = TestEvidence();
        var provider = new CountingProvider(evidence, transientFailures: 3);
        var evaluator = new CountingEvaluator(PassingEvaluation());

        var result = await ExecuteAsync(provider, evaluator);

        Assert.Equal(BranchOutcome.TransientFailure, result.Outcome);
        Assert.Equal(evidence.Id, result.EvidenceId);
        Assert.Equal(3, provider.CallCount);
        Assert.Equal(0, evaluator.CallCount);
        Assert.Equal(3, result.Attempts.Length);
        Assert.All(result.Attempts, detail => Assert.Contains("known transient failure", detail));
    }

    [Fact]
    public async Task Unclassified_provider_failures_remain_technical_failures()
    {
        var provider = new CountingProvider(TestEvidence(), unclassifiedFailure: true);
        var evaluator = new CountingEvaluator(PassingEvaluation());

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ExecuteAsync(provider, evaluator));

        Assert.Equal(1, provider.CallCount);
        Assert.Equal(0, evaluator.CallCount);
    }

    [Fact]
    public async Task Policy_failures_remain_technical_failures()
    {
        var provider = new CountingProvider(TestEvidence());
        var evaluator = new CountingEvaluator(PassingEvaluation(), throwOnEvaluation: true);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => ExecuteAsync(provider, evaluator));

        Assert.Equal(1, provider.CallCount);
        Assert.Equal(1, evaluator.CallCount);
    }

    private static Task<BranchResult> ExecuteAsync(
        IEvidenceProvider<TestEvidenceRecord> provider,
        CountingEvaluator evaluator) =>
        BranchExecution.ExecuteAsync(
            Guid.NewGuid(),
            Submission(),
            WorkItem(),
            ReadinessCheck.Test,
            EvidenceKind.Test,
            provider,
            evaluator.Evaluate,
            CancellationToken.None);

    private static async Task AssertTransientThenEvidence<TEvidence>(
        IEvidenceProvider<TEvidence> provider,
        TEvidence expected)
        where TEvidence : EvidenceRecord
    {
        var exception = await Assert.ThrowsAsync<KnownTransientEvidenceProviderException>(
            async () => await provider.GetCurrentAsync(Id(), CancellationToken.None));
        var current = await provider.GetCurrentAsync(Id(), CancellationToken.None);

        Assert.Equal(expected.Id, exception.EvidenceId);
        Assert.Same(expected, current);
    }

    private static Evaluation PassingEvaluation() => new(
        BranchOutcome.Passed,
        new Dictionary<string, string>
        {
            ["ready"] = "Evidence satisfies the policy.",
        });

    private static BranchWorkItem WorkItem() => new(
        Id(),
        roundNumber: 1,
        ReadinessCheck.Test,
        WorkDisposition.Execute,
        PlanningReason.InitialEvaluation,
        "Executed because this is the initial evaluation.");

    private static ReleaseSubmission Submission() => new(
        Id(),
        "orders",
        "2.4.0",
        new UtcInterval(Utc(2026, 8, 20, 20), Utc(2026, 8, 20, 21)),
        Utc(2026, 8, 17, 7));

    private static TestEvidenceRecord TestEvidence() => new(
        Guid.NewGuid(),
        Id(),
        1,
        Utc(2026, 8, 17, 9),
        null,
        "2.4.0",
        Utc(2026, 8, 17, 8),
        0.98m,
        []);

    private static SecurityEvidenceRecord SecurityEvidence() => new(
        Guid.NewGuid(),
        Id(),
        1,
        Utc(2026, 8, 17, 9),
        null,
        "2.4.0",
        Utc(2026, 8, 17, 8),
        [],
        [],
        new Dictionary<string, (string Scope, UtcInstant ExpiresAt)>());

    private static ChangeEvidenceRecord ChangeEvidence() => new(
        Guid.NewGuid(),
        Id(),
        1,
        Utc(2026, 8, 17, 9),
        null,
        isApproved: true,
        new UtcInterval(Utc(2026, 8, 17, 9), Utc(2026, 8, 17, 12)));

    private static ReleaseId Id() => new("release-42");

    private static UtcInstant Utc(int year, int month, int day, int hour) =>
        new(new DateTimeOffset(year, month, day, hour, 0, 0, TimeSpan.Zero));

    private sealed class CountingProvider(
        TestEvidenceRecord? evidence,
        int transientFailures = 0,
        bool unclassifiedFailure = false) : IEvidenceProvider<TestEvidenceRecord>
    {
        public int CallCount { get; private set; }

        public ValueTask<TestEvidenceRecord?> GetCurrentAsync(
            ReleaseId releaseId,
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
                    "The simulated evidence source is temporarily unavailable.");
            }

            return ValueTask.FromResult(evidence);
        }
    }

    private sealed class CountingEvaluator(
        Evaluation evaluation,
        bool throwOnEvaluation = false)
    {
        public int CallCount { get; private set; }

        public Evaluation Evaluate(
            ReleaseSubmission submission,
            TestEvidenceRecord evidence)
        {
            CallCount++;
            if (throwOnEvaluation)
            {
                throw new InvalidOperationException("Policy invariant failure.");
            }

            return evaluation;
        }
    }

    private sealed record Evaluation(
        BranchOutcome Outcome,
        IReadOnlyDictionary<string, string> FindingValues) : IReadinessPolicyEvaluation
    {
        public ImmutableDictionary<string, string> Findings { get; } =
            FindingValues.ToImmutableDictionary();
    }
}
