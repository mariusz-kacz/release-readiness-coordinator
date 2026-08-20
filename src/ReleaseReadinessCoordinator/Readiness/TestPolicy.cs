using System.Collections.Immutable;
using ReleaseReadinessCoordinator.Domain;

namespace ReleaseReadinessCoordinator.Readiness;

internal interface ITestReadinessPolicy
{
    TestPolicyEvaluation Evaluate(
        ReleaseSubmission submission,
        TestEvidenceRecord evidence);
}

internal sealed class TestReadinessPolicy : ITestReadinessPolicy
{
    private const decimal MinimumPassRate = 0.95m;

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
                missing);
        }

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

        return blockers.Count > 0
            ? new TestPolicyEvaluation(BranchOutcome.Blocked, blockers)
            : new TestPolicyEvaluation(
                BranchOutcome.Passed,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["ready"] = "Test evidence satisfies the version, pass-rate, and critical-suite policy.",
                });
    }
}

internal sealed record TestPolicyEvaluation : IReadinessPolicyEvaluation
{
    public TestPolicyEvaluation(
        BranchOutcome outcome,
        IReadOnlyDictionary<string, string> findings)
    {
        if (outcome is BranchOutcome.TransientFailure)
        {
            throw new ArgumentException(
                "Provider failures are classified by branch execution, not by deterministic policy.",
                nameof(outcome));
        }

        Outcome = outcome;
        Findings = findings.ToImmutableDictionary(StringComparer.Ordinal);
    }

    public BranchOutcome Outcome { get; }

    public ImmutableDictionary<string, string> Findings { get; }
}
