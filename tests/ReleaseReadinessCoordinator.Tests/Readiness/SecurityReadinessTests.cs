using Microsoft.Agents.AI.Workflows;
using ReleaseReadinessCoordinator.Domain;
using ReleaseReadinessCoordinator.Readiness;
using ReleaseReadinessCoordinator.Workflow;

namespace ReleaseReadinessCoordinator.Tests.Readiness;

public sealed class SecurityReadinessPolicyTests
{
    private static readonly UtcInstant ScannedAt = Utc(2026, 8, 17, 8);
    private static readonly UtcInstant FreshnessDeadline = Utc(2026, 8, 18, 8);
    private static readonly UtcInstant WindowEnd = Utc(2026, 8, 17, 11);

    [Fact]
    public void Required_security_facts_must_all_be_present()
    {
        var policy = PolicyAt(Utc(2026, 8, 17, 9).Value);
        SecurityEvidenceRecord[] evidenceWithMissingFacts =
        [
            Evidence(scanVersion: null),
            new SecurityEvidenceRecord(
                Guid.NewGuid(), RevisionKey(), 1, Utc(2026, 8, 17, 9), null,
                "2.4.0", scannedAt: null, [], [], EmptyExceptions()),
            new SecurityEvidenceRecord(
                Guid.NewGuid(), RevisionKey(), 1, Utc(2026, 8, 17, 9), null,
                "2.4.0", ScannedAt, unresolvedCriticalFindingIds: null, [], EmptyExceptions()),
            new SecurityEvidenceRecord(
                Guid.NewGuid(), RevisionKey(), 1, Utc(2026, 8, 17, 9), null,
                "2.4.0", ScannedAt, [], unresolvedHighFindingIds: null, EmptyExceptions()),
            new SecurityEvidenceRecord(
                Guid.NewGuid(), RevisionKey(), 1, Utc(2026, 8, 17, 9), null,
                "2.4.0", ScannedAt, [], [], approvedExceptions: null),
        ];

        Assert.All(
            evidenceWithMissingFacts,
            evidence => Assert.Equal(
                BranchOutcome.MissingEvidence,
                policy.Evaluate(Submission(), evidence).Outcome));
    }

    [Fact]
    public void Exact_version_with_no_findings_passes_until_the_freshness_deadline()
    {
        var policy = PolicyAt(FreshnessDeadline.Value.AddTicks(-1));

        var evaluation = policy.Evaluate(
            Submission(releaseVersion: "2.4.0"),
            Evidence(scanVersion: "2.4.0"));

        Assert.Equal(BranchOutcome.Passed, evaluation.Outcome);
        Assert.Equal(FreshnessDeadline, evaluation.ValidUntil);
    }

    [Fact]
    public void Matching_exception_with_exact_scope_and_window_end_expiry_passes()
    {
        var policy = PolicyAt(Utc(2026, 8, 17, 9).Value);
        var exceptions = new Dictionary<string, (string Scope, UtcInstant ExpiresAt)>
        {
            ["HIGH-1"] = ("orders", WindowEnd),
        };

        var evaluation = policy.Evaluate(
            Submission(),
            Evidence(highFindings: ["HIGH-1"], approvedExceptions: exceptions));

        Assert.Equal(BranchOutcome.Passed, evaluation.Outcome);
        Assert.Equal(WindowEnd, evaluation.ValidUntil);
    }

    [Fact]
    public void High_finding_without_a_matching_exception_blocks()
    {
        var policy = PolicyAt(Utc(2026, 8, 17, 9).Value);

        var evaluation = policy.Evaluate(
            Submission(),
            Evidence(highFindings: ["HIGH-1"]));

        Assert.Equal(BranchOutcome.Blocked, evaluation.Outcome);
    }

    [Theory]
    [InlineData("Orders", 0, 0)]
    [InlineData("orders", -1, 0)]
    [InlineData("orders", 0, 1)]
    public void Exception_scope_expiry_and_release_window_boundaries_block(
        string exceptionScope,
        long expiryTicksFromWindowEnd,
        long requestedEndTicksAfterWindowEnd)
    {
        var policy = PolicyAt(Utc(2026, 8, 17, 9).Value);
        var exceptions = new Dictionary<string, (string Scope, UtcInstant ExpiresAt)>
        {
            ["HIGH-1"] = (
                exceptionScope,
                new UtcInstant(WindowEnd.Value.AddTicks(expiryTicksFromWindowEnd))),
        };

        var evaluation = policy.Evaluate(
            Submission(requestedEndTicksAfterWindowEnd: requestedEndTicksAfterWindowEnd),
            Evidence(highFindings: ["HIGH-1"], approvedExceptions: exceptions));

        Assert.Equal(BranchOutcome.Blocked, evaluation.Outcome);
        Assert.Null(evaluation.ValidUntil);
    }

    [Fact]
    public void Unresolved_critical_finding_always_blocks()
    {
        var policy = PolicyAt(Utc(2026, 8, 17, 9).Value);
        var exceptions = new Dictionary<string, (string Scope, UtcInstant ExpiresAt)>
        {
            ["CRIT-1"] = ("orders", WindowEnd),
        };

        var evaluation = policy.Evaluate(
            Submission(),
            Evidence(criticalFindings: ["CRIT-1"], approvedExceptions: exceptions));

        Assert.Equal(BranchOutcome.Blocked, evaluation.Outcome);
    }

    [Fact]
    public void Version_must_match_exactly()
    {
        var policy = PolicyAt(Utc(2026, 8, 17, 9).Value);

        var evaluation = policy.Evaluate(
            Submission(releaseVersion: "2.4.0"),
            Evidence(scanVersion: "2.4.0-rc.1"));

        Assert.Equal(BranchOutcome.Blocked, evaluation.Outcome);
    }

    [Theory]
    [InlineData(-1, BranchOutcome.Passed)]
    [InlineData(0, BranchOutcome.Blocked)]
    public void Freshness_is_current_only_before_the_deadline(
        long ticksFromDeadline,
        BranchOutcome expected)
    {
        var policy = PolicyAt(FreshnessDeadline.Value.AddTicks(ticksFromDeadline));

        var evaluation = policy.Evaluate(Submission(), Evidence());

        Assert.Equal(expected, evaluation.Outcome);
    }

    private static SecurityReadinessPolicy PolicyAt(DateTimeOffset now) =>
        new(new FixedTimeProvider(now));

    private static ReleaseSubmission Submission(
        string releaseVersion = "2.4.0",
        long requestedEndTicksAfterWindowEnd = 0) => new(
            RevisionKey(),
            "orders",
            releaseVersion,
            new UtcInterval(
                Utc(2026, 8, 17, 10),
                new UtcInstant(WindowEnd.Value.AddTicks(requestedEndTicksAfterWindowEnd))),
            Utc(2026, 8, 17, 7));

    private static SecurityEvidenceRecord Evidence(
        string? scanVersion = "2.4.0",
        IEnumerable<string>? criticalFindings = default,
        IEnumerable<string>? highFindings = default,
        IReadOnlyDictionary<string, (string Scope, UtcInstant ExpiresAt)>? approvedExceptions = default) => new(
            Guid.NewGuid(),
            RevisionKey(),
            1,
            Utc(2026, 8, 17, 9),
            null,
            scanVersion,
            ScannedAt,
            criticalFindings ?? [],
            highFindings ?? [],
            approvedExceptions ?? EmptyExceptions());

    private static IReadOnlyDictionary<string, (string Scope, UtcInstant ExpiresAt)> EmptyExceptions() =>
        new Dictionary<string, (string Scope, UtcInstant ExpiresAt)>(StringComparer.Ordinal);

    private static ReleaseRevisionKey RevisionKey() => new("release-42", 3);

    private static UtcInstant Utc(int year, int month, int day, int hour) =>
        new(new DateTimeOffset(year, month, day, hour, 0, 0, TimeSpan.Zero));

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}

public sealed class SecurityReadinessBranchExecutionTests
{
    private static readonly UtcInstant ScannedAt = Utc(2026, 8, 17, 8);

    [Fact]
    public async Task Simulated_provider_returns_configured_evidence_after_known_transient_failures()
    {
        var evidence = Evidence();
        var provider = new SimulatedSecurityEvidenceProvider(
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
        var policy = new CountingPolicy(new SecurityPolicyEvaluation(
            BranchOutcome.Blocked,
            validUntil: null,
            new Dictionary<string, string>
            {
                ["critical-findings"] = "An unresolved critical finding blocks readiness.",
            }));
        var executor = Executor(provider, policy);

        var result = await executor.ExecuteAsync(Submission(), WorkItem());

        Assert.Equal(BranchOutcome.Blocked, result.Outcome);
        Assert.Equal(1, provider.CallCount);
        Assert.Equal(1, policy.CallCount);
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

    private static SecurityReadinessBranchExecutor Executor(
        ISecurityEvidenceProvider provider,
        ISecurityReadinessPolicy policy) => new(provider, policy);

    private static SecurityPolicyEvaluation PassingEvaluation() => new(
        BranchOutcome.Passed,
        Utc(2026, 8, 18, 8),
        new Dictionary<string, string>
        {
            ["ready"] = "Security evidence satisfies the policy.",
        });

    private static ReleaseReadinessCoordinator.Domain.BranchWorkItem WorkItem() => new(
        RevisionKey(),
        roundNumber: 1,
        ReadinessCheck.Security,
        WorkDisposition.Execute,
        PlanningReason.InitialEvaluation,
        "Executed because this is the initial evaluation.");

    private static ReleaseSubmission Submission() => new(
        RevisionKey(),
        "orders",
        "2.4.0",
        new UtcInterval(Utc(2026, 8, 17, 10), Utc(2026, 8, 17, 11)),
        Utc(2026, 8, 17, 7));

    private static SecurityEvidenceRecord Evidence() => new(
        Guid.NewGuid(),
        RevisionKey(),
        1,
        Utc(2026, 8, 17, 9),
        null,
        "2.4.0",
        ScannedAt,
        [],
        [],
        new Dictionary<string, (string Scope, UtcInstant ExpiresAt)>());

    private static ReleaseRevisionKey RevisionKey() => new("release-42", 3);

    private static UtcInstant Utc(int year, int month, int day, int hour) =>
        new(new DateTimeOffset(year, month, day, hour, 0, 0, TimeSpan.Zero));

    private sealed class CountingProvider(
        SecurityEvidenceRecord? evidence,
        int transientFailures = 0,
        bool unclassifiedFailure = false) : ISecurityEvidenceProvider
    {
        public int CallCount { get; private set; }

        public ValueTask<SecurityEvidenceRecord?> GetCurrentAsync(
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
                    "The simulated Security source is temporarily unavailable.");
            }

            return ValueTask.FromResult(evidence);
        }
    }

    private sealed class CountingPolicy(SecurityPolicyEvaluation evaluation) : ISecurityReadinessPolicy
    {
        public int CallCount { get; private set; }

        public SecurityPolicyEvaluation Evaluate(
            ReleaseSubmission submission,
            SecurityEvidenceRecord evidence)
        {
            CallCount++;
            return evaluation;
        }
    }
}

public sealed class SecurityReadinessWorkflowIntegrationTests
{
    [Fact]
    public async Task Real_graph_security_branch_uses_provider_and_policy_instead_of_simulated_outcome()
    {
        var submission = Submission();
        var securityEvidence = SecurityEvidence(submission.Key);
        var securityProvider = new CountingProvider(securityEvidence);
        var securityPolicy = new CountingPolicy();
        await using var host = await ReadinessWorkflowTestHost.CreateWithSecurityAsync(
            submission,
            securityEvidence,
            securityProvider,
            securityPolicy);

        await using var run = await InProcessExecution.RunAsync(
            host.CreateWorkflow(),
            host.Input);

        Assert.DoesNotContain(
            run.NewEvents.OfType<WorkflowOutputEvent>(),
            output => output.Data is EvaluationRound);
        var detail = await host.DataService.GetReleaseDetailAsync(host.Submission.Key);
        var round = Assert.Single(detail!.EvaluationRounds);
        var securityResult = Assert.Single(
            round.Results,
            result => result.Check is ReadinessCheck.Security);
        Assert.Equal(BranchOutcome.Passed, securityResult.Outcome);
        Assert.Equal(securityEvidence.Id, securityResult.EvidenceId);
        Assert.Equal(1, securityProvider.CallCount);
        Assert.Equal(1, securityPolicy.CallCount);
    }

    private static ReleaseSubmission Submission() => new(
        new ReleaseRevisionKey("release-graph", 1),
        "orders",
        "2.4.0",
        new UtcInterval(Utc(2026, 8, 17, 10), Utc(2026, 8, 17, 11)),
        Utc(2026, 8, 17, 7));

    private static SecurityEvidenceRecord SecurityEvidence(ReleaseRevisionKey releaseRevision) => new(
        Guid.NewGuid(),
        releaseRevision,
        1,
        Utc(2026, 8, 17, 9),
        null,
        "2.4.0",
        Utc(2026, 8, 17, 8),
        [],
        [],
        new Dictionary<string, (string Scope, UtcInstant ExpiresAt)>());

    private static UtcInstant Utc(int year, int month, int day, int hour) =>
        new(new DateTimeOffset(year, month, day, hour, 0, 0, TimeSpan.Zero));

    private sealed class CountingProvider(SecurityEvidenceRecord evidence) : ISecurityEvidenceProvider
    {
        public int CallCount { get; private set; }

        public ValueTask<SecurityEvidenceRecord?> GetCurrentAsync(
            ReleaseRevisionKey releaseRevision,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return ValueTask.FromResult<SecurityEvidenceRecord?>(evidence);
        }
    }

    private sealed class CountingPolicy : ISecurityReadinessPolicy
    {
        public int CallCount { get; private set; }

        public SecurityPolicyEvaluation Evaluate(
            ReleaseSubmission submission,
            SecurityEvidenceRecord evidence)
        {
            CallCount++;
            return new SecurityPolicyEvaluation(
                BranchOutcome.Passed,
                Utc(2026, 8, 18, 8),
                new Dictionary<string, string>
                {
                    ["ready"] = "Security evidence satisfies the policy.",
                });
        }
    }

}
