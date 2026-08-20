using System.Collections.Immutable;
using ReleaseReadinessCoordinator.Domain;

namespace ReleaseReadinessCoordinator.Readiness;

internal interface ISecurityReadinessPolicy
{
    SecurityPolicyEvaluation Evaluate(
        ReleaseSubmission submission,
        SecurityEvidenceRecord evidence);
}

internal sealed class SecurityReadinessPolicy : ISecurityReadinessPolicy
{
    public SecurityPolicyEvaluation Evaluate(
        ReleaseSubmission submission,
        SecurityEvidenceRecord evidence)
    {
        ArgumentNullException.ThrowIfNull(submission);
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.ReleaseId != submission.ReleaseId)
        {
            throw new InvalidOperationException(
                "Security evidence and release submission identify different revisions.");
        }

        var missing = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(evidence.ScanVersion))
        {
            missing["scan-version"] = "The Security scan version is missing.";
        }

        if (!evidence.ScannedAt.HasValue)
        {
            missing["scanned-at"] = "The Security scan time is missing.";
        }

        if (!evidence.UnresolvedCriticalFindingIds.HasValue)
        {
            missing["critical-findings"] = "The unresolved critical-finding facts are missing.";
        }

        if (!evidence.UnresolvedHighFindingIds.HasValue)
        {
            missing["high-findings"] = "The unresolved high-finding facts are missing.";
        }

        if (evidence.ApprovedExceptions is null)
        {
            missing["approved-exceptions"] = "The approved Security exception facts are missing.";
        }

        if (missing.Count > 0)
        {
            return new SecurityPolicyEvaluation(
                BranchOutcome.MissingEvidence,
                missing);
        }

        var blockers = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!string.Equals(evidence.ScanVersion, submission.ReleaseVersion, StringComparison.Ordinal))
        {
            blockers["scan-version"] =
                $"Security scan version '{evidence.ScanVersion}' does not exactly match release version '{submission.ReleaseVersion}'.";
        }

        if (!evidence.UnresolvedCriticalFindingIds!.Value.IsEmpty)
        {
            blockers["critical-findings"] =
                $"Unresolved critical findings: {string.Join(", ", evidence.UnresolvedCriticalFindingIds.Value)}.";
        }

        foreach (var findingId in evidence.UnresolvedHighFindingIds!.Value.Distinct(StringComparer.Ordinal))
        {
            if (!evidence.ApprovedExceptions!.TryGetValue(findingId, out var approvedException))
            {
                blockers[$"high-finding:{findingId}"] =
                    $"High finding '{findingId}' has no approved exception.";
                continue;
            }

            if (!string.Equals(approvedException.Scope, submission.ServiceName, StringComparison.Ordinal))
            {
                blockers[$"high-finding:{findingId}"] =
                    $"High finding '{findingId}' has an exception outside service scope '{submission.ServiceName}'.";
                continue;
            }

            if (approvedException.ExpiresAt < submission.RequestedDeploymentWindow.End)
            {
                blockers[$"high-finding:{findingId}"] =
                    $"High finding '{findingId}' has an exception that expires before the requested deployment window ends.";
                continue;
            }

        }

        return blockers.Count > 0
            ? new SecurityPolicyEvaluation(BranchOutcome.Blocked, blockers)
            : new SecurityPolicyEvaluation(
                BranchOutcome.Passed,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["ready"] = "Security evidence satisfies the version, findings, and exception policy.",
                });
    }
}

internal sealed record SecurityPolicyEvaluation : IReadinessPolicyEvaluation
{
    public SecurityPolicyEvaluation(
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
