using System.Collections.Immutable;
using ReleaseReadinessCoordinator.Domain;

namespace ReleaseReadinessCoordinator.Readiness;

internal interface ITestReadinessPolicy
{
    TestPolicyEvaluation Evaluate(
        ReleaseSubmission submission,
        TestEvidenceRecord evidence);
}

internal sealed class TestReadinessPolicy(TimeProvider timeProvider) : ITestReadinessPolicy
{
    private const decimal MinimumPassRate = 0.95m;

    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public TestPolicyEvaluation Evaluate(
        ReleaseSubmission submission,
        TestEvidenceRecord evidence)
    {
        ArgumentNullException.ThrowIfNull(submission);
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.ReleaseId != submission.ReleaseId)
        {
            throw new InvalidOperationException(
                "Test evidence and release submission identify different revisions.");
        }

        var missing = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(evidence.TestRunVersion))
        {
            missing["test-run-version"] = "The Test run version is missing.";
        }

        if (!evidence.CompletedAt.HasValue)
        {
            missing["completed-at"] = "The Test completion time is missing.";
        }

        if (!evidence.PassRate.HasValue)
        {
            missing["pass-rate"] = "The Test pass rate is missing.";
        }

        if (!evidence.CriticalSuiteFailures.HasValue)
        {
            missing["critical-suite-failures"] = "The critical-suite result is missing.";
        }

        if (missing.Count > 0)
        {
            return new TestPolicyEvaluation(
                BranchOutcome.MissingEvidence,
                validUntil: null,
                missing);
        }

        var validUntil = FreshnessDeadlines.Calculate(evidence);
        var blockers = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.Equals(
                evidence.TestRunVersion,
                submission.ReleaseVersion,
                StringComparison.Ordinal))
        {
            blockers["test-run-version"] =
                $"Test run version '{evidence.TestRunVersion}' does not exactly match release version '{submission.ReleaseVersion}'.";
        }

        if (evidence.PassRate!.Value < MinimumPassRate)
        {
            blockers["pass-rate"] = "The Test pass rate is below 95%.";
        }

        if (!evidence.CriticalSuiteFailures!.Value.IsEmpty)
        {
            blockers["critical-suite-failures"] =
                $"Critical suites failed: {string.Join(", ", evidence.CriticalSuiteFailures.Value)}.";
        }

        if (!FreshnessDeadlines.IsCurrent(validUntil, _timeProvider))
        {
            blockers["freshness"] = $"Test evidence reached its validity deadline at {validUntil.ToDisplayString()}.";
        }

        return blockers.Count > 0
            ? new TestPolicyEvaluation(BranchOutcome.Blocked, validUntil: null, blockers)
            : new TestPolicyEvaluation(
                BranchOutcome.Passed,
                validUntil,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["ready"] = "Test evidence satisfies the version, pass-rate, critical-suite, and freshness policy.",
                });
    }
}

internal sealed record TestPolicyEvaluation : IReadinessPolicyEvaluation
{
    public TestPolicyEvaluation(
        BranchOutcome outcome,
        UtcInstant? validUntil,
        IReadOnlyDictionary<string, string> findings)
    {
        if (outcome is BranchOutcome.TransientFailure)
        {
            throw new ArgumentException(
                "Provider failures are classified by branch execution, not by deterministic policy.",
                nameof(outcome));
        }

        if ((outcome is BranchOutcome.Passed) != validUntil.HasValue)
        {
            throw new ArgumentException(
                "Only a passing Test policy evaluation has a validity deadline.",
                nameof(validUntil));
        }

        Outcome = outcome;
        ValidUntil = validUntil;
        Findings = findings.ToImmutableDictionary(StringComparer.Ordinal);
    }

    public BranchOutcome Outcome { get; }

    public UtcInstant? ValidUntil { get; }

    public ImmutableDictionary<string, string> Findings { get; }
}
