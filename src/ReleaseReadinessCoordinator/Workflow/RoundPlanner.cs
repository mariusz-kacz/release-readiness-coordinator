using System.Collections.Immutable;
using ReleaseReadinessCoordinator.Domain;
using DomainBranchResult = ReleaseReadinessCoordinator.Domain.BranchResult;
using DomainBranchWorkItem = ReleaseReadinessCoordinator.Domain.BranchWorkItem;

namespace ReleaseReadinessCoordinator.Workflow;

internal sealed class RoundPlanner(TimeProvider timeProvider)
{
    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public ImmutableArray<DomainBranchWorkItem> Plan(RoundPlanningRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        return
        [
            Plan(ReadinessCheck.Test, request),
            Plan(ReadinessCheck.Security, request),
            Plan(ReadinessCheck.Change, request),
        ];
    }

    private DomainBranchWorkItem Plan(ReadinessCheck check, RoundPlanningRequest request)
    {
        Guid? currentEvidenceId = request.CurrentEvidenceIds.TryGetValue(check, out var value)
            ? value
            : null;

        if (!request.PreviousResults.TryGetValue(check, out var previous))
        {
            return Execute(
                check,
                request,
                PlanningReason.InitialEvaluation,
                $"Executed because no prior {check} result exists.");
        }

        var explicitlySelected = request.ExplicitlySelectedChecks.Contains(check);
        var evidenceChanged = previous.EvidenceId != currentEvidenceId;
        var previousResultNotPassed = previous.Outcome is not BranchOutcome.Passed;
        var expired = previous.ValidUntil.HasValue
            && !FreshnessDeadlines.IsCurrent(previous.ValidUntil.Value, _timeProvider);

        var reason = SelectReason(
            explicitlySelected,
            evidenceChanged,
            previousResultNotPassed,
            expired);
        var detail = Explain(
            check,
            previous,
            currentEvidenceId,
            reason,
            explicitlySelected,
            evidenceChanged,
            previousResultNotPassed,
            expired);

        return reason is PlanningReason.StillCurrent
            ? new DomainBranchWorkItem(
                request.ReleaseId,
                request.RoundNumber,
                check,
                WorkDisposition.Reuse,
                reason,
                detail,
                previous.Id)
            : Execute(check, request, reason, detail);
    }

    private static PlanningReason SelectReason(
        bool explicitlySelected,
        bool evidenceChanged,
        bool previousResultNotPassed,
        bool expired)
    {
        if (explicitlySelected)
        {
            return PlanningReason.ExplicitlySelected;
        }

        if (evidenceChanged)
        {
            return PlanningReason.EvidenceChanged;
        }

        if (previousResultNotPassed)
        {
            return PlanningReason.PreviousResultNotPassed;
        }

        return expired ? PlanningReason.Expired : PlanningReason.StillCurrent;
    }

    private static DomainBranchWorkItem Execute(
        ReadinessCheck check,
        RoundPlanningRequest request,
        PlanningReason reason,
        string detail) => new(
        request.ReleaseId,
        request.RoundNumber,
        check,
        WorkDisposition.Execute,
        reason,
        detail);

    private static string Explain(
        ReadinessCheck check,
        DomainBranchResult previous,
        Guid? currentEvidenceId,
        PlanningReason reason,
        bool explicitlySelected,
        bool evidenceChanged,
        bool previousResultNotPassed,
        bool expired)
    {
        var primary = reason switch
        {
            PlanningReason.ExplicitlySelected =>
                $"Executed because {check} was explicitly selected for rerun.",
            PlanningReason.EvidenceChanged =>
                ExplainEvidenceChange(
                    check,
                    previous.EvidenceId.HasValue,
                    currentEvidenceId.HasValue),
            PlanningReason.PreviousResultNotPassed =>
                $"Executed because the previous {check} result was {previous.Outcome}.",
            PlanningReason.Expired =>
                $"Executed because the previous {check} result reached its validity deadline at {previous.ValidUntil!.Value.ToDisplayString()}.",
            PlanningReason.StillCurrent =>
                ExplainReuse(previous.RoundNumber),
            _ => throw new InvalidOperationException($"Unsupported planning reason '{reason}'."),
        };

        var additional = new List<string>();
        AddAdditional(additional, reason, PlanningReason.ExplicitlySelected, explicitlySelected, "explicitly selected");
        AddAdditional(additional, reason, PlanningReason.EvidenceChanged, evidenceChanged, "evidence changed");
        AddAdditional(additional, reason, PlanningReason.PreviousResultNotPassed, previousResultNotPassed, "previous result did not pass");
        AddAdditional(additional, reason, PlanningReason.Expired, expired, "deadline reached");

        return additional.Count == 0
            ? primary
            : $"{primary} Additional facts: {string.Join(", ", additional)}.";
    }

    internal static string ExplainEvidenceChange(
        ReadinessCheck check,
        bool hadPreviousEvidence,
        bool hasCurrentEvidence)
    {
        if (!hadPreviousEvidence)
        {
            return $"Executed because {check} evidence is now available. The previous result had no {check} evidence.";
        }

        return hasCurrentEvidence
            ? $"Executed because the {check} evidence was updated since the previous result."
            : $"Executed because the {check} evidence used by the previous result is no longer available.";
    }

    internal static string ExplainReuse(int sourceRound)
    {
        if (sourceRound <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sourceRound));
        }

        return $"Reused from round {sourceRound} because the evidence is unchanged and the previous passing result is still valid.";
    }

    private static void AddAdditional(
        ICollection<string> additional,
        PlanningReason selectedReason,
        PlanningReason conditionReason,
        bool applies,
        string detail)
    {
        if (applies && selectedReason != conditionReason)
        {
            additional.Add(detail);
        }
    }

}
