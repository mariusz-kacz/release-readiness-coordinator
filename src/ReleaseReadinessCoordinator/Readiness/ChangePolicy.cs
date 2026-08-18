using System.Collections.Immutable;
using ReleaseReadinessCoordinator.Domain;

namespace ReleaseReadinessCoordinator.Readiness;

internal interface IChangeReadinessPolicy
{
    ChangePolicyEvaluation Evaluate(
        ReleaseSubmission submission,
        ChangeEvidenceRecord evidence);
}

internal sealed class ChangeReadinessPolicy : IChangeReadinessPolicy
{
    public ChangePolicyEvaluation Evaluate(
        ReleaseSubmission submission,
        ChangeEvidenceRecord evidence)
    {
        ArgumentNullException.ThrowIfNull(submission);
        ArgumentNullException.ThrowIfNull(evidence);
        if (evidence.ReleaseRevision != submission.Key)
        {
            throw new InvalidOperationException(
                "Change evidence and release submission identify different revisions.");
        }

        var missing = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!evidence.IsApproved.HasValue)
        {
            missing["approval"] = "The Change approval state is missing.";
        }

        if (evidence.ApprovedWindow is null)
        {
            missing["approved-window"] = "The approved Change deployment window is missing.";
        }

        if (missing.Count > 0)
        {
            return new ChangePolicyEvaluation(
                BranchOutcome.MissingEvidence,
                validUntil: null,
                missing);
        }

        var blockers = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!evidence.IsApproved!.Value)
        {
            blockers["approval"] = "The Change is not approved.";
        }

        var approvedWindow = evidence.ApprovedWindow!;
        var requestedWindow = submission.RequestedDeploymentWindow;
        if (requestedWindow.Start < approvedWindow.Start || requestedWindow.End > approvedWindow.End)
        {
            blockers["approved-window"] =
                "The requested deployment window is not wholly inside the approved Change window.";
        }

        return blockers.Count > 0
            ? new ChangePolicyEvaluation(BranchOutcome.Blocked, validUntil: null, blockers)
            : new ChangePolicyEvaluation(
                BranchOutcome.Passed,
                approvedWindow.End,
                new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["ready"] = "Change evidence is approved and fully contains the requested deployment window.",
                });
    }
}

internal sealed record ChangePolicyEvaluation : IReadinessPolicyEvaluation
{
    public ChangePolicyEvaluation(
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
                "Only a passing Change policy evaluation has a validity deadline.",
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
