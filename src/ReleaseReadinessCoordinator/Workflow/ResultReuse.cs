using ReleaseReadinessCoordinator.Domain;
using DomainBranchResult = ReleaseReadinessCoordinator.Domain.BranchResult;
using DomainBranchWorkItem = ReleaseReadinessCoordinator.Domain.BranchWorkItem;

namespace ReleaseReadinessCoordinator.Workflow;

internal sealed class ResultReuse(TimeProvider timeProvider)
{
    private readonly TimeProvider _timeProvider =
        timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));

    public DomainBranchResult Create(
        Guid resultId,
        DomainBranchWorkItem workItem,
        DomainBranchResult source,
        Guid? currentEvidenceId)
    {
        ArgumentNullException.ThrowIfNull(workItem);
        ArgumentNullException.ThrowIfNull(source);
        if (resultId == Guid.Empty || resultId == source.Id)
        {
            throw new InvalidOperationException("Reuse must emit a distinct, non-empty result identity.");
        }

        if (workItem.Disposition is not WorkDisposition.Reuse
            || workItem.PlanningReason is not PlanningReason.StillCurrent
            || workItem.ReuseSourceResultId != source.Id)
        {
            throw new InvalidOperationException(
                "The reuse work item does not identify the supplied source result.");
        }

        if (source.ReleaseId != workItem.ReleaseId
            || source.Check != workItem.Check
            || source.RoundNumber >= workItem.RoundNumber)
        {
            throw new InvalidOperationException(
                "The reuse source does not belong to this branch, release, and an earlier round.");
        }

        if (source.Outcome is not BranchOutcome.Passed
            || !source.EvidenceId.HasValue
            || !source.ValidUntil.HasValue)
        {
            throw new InvalidOperationException("Only a complete passing result can be reused.");
        }

        if (currentEvidenceId == Guid.Empty || source.EvidenceId != currentEvidenceId)
        {
            throw new InvalidOperationException(
                "Defensive reuse verification found that the current evidence identity changed.");
        }

        if (!FreshnessDeadlines.IsCurrent(source.ValidUntil.Value, _timeProvider))
        {
            throw new InvalidOperationException(
                "Defensive reuse verification found that the source result reached its validity deadline.");
        }

        return new DomainBranchResult(
            resultId,
            workItem.ReleaseId,
            workItem.RoundNumber,
            workItem.Check,
            BranchOutcome.Passed,
            ExecutionDisposition.Reused,
            PlanningReason.StillCurrent,
            RoundPlanner.ExplainReuse(source.RoundNumber),
            source.EvidenceId,
            source.EvidenceKind,
            source.ValidUntil,
            attempts: [],
            source.Findings,
            source.Id,
            source.RoundNumber);
    }
}
