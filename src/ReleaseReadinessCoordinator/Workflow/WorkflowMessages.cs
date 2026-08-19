using ReleaseReadinessCoordinator.Domain;
using System.Collections.Immutable;
using DomainBranchResult = ReleaseReadinessCoordinator.Domain.BranchResult;
using DomainBranchWorkItem = ReleaseReadinessCoordinator.Domain.BranchWorkItem;

namespace ReleaseReadinessCoordinator.Workflow;

public enum ReadinessBranch
{
    Test = 1,
    Security = 2,
    Change = 3,
}

public sealed record ApprovalRequest(
    DecisionSnapshot Snapshot,
    HumanDecisionRequest Request)
{
    public int RoundNumber => Snapshot.RoundNumber;
}

public sealed record ApprovalResponse(HumanResponse Response);

internal sealed record RoundPlanningRequest
{
    public RoundPlanningRequest(
        ReleaseId releaseId,
        int roundNumber,
        IEnumerable<DomainBranchResult> previousResults,
        IReadOnlyDictionary<ReadinessCheck, Guid> currentEvidenceIds,
        IEnumerable<ReadinessCheck> explicitlySelectedChecks)
    {
        ArgumentNullException.ThrowIfNull(releaseId);
        if (roundNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(roundNumber));
        }

        ReleaseId = releaseId;
        RoundNumber = roundNumber;
        PreviousResults = CopyPreviousResults(previousResults, releaseId, roundNumber);
        CurrentEvidenceIds = CopyCurrentEvidenceIds(currentEvidenceIds);
        ArgumentNullException.ThrowIfNull(explicitlySelectedChecks);
        ExplicitlySelectedChecks = explicitlySelectedChecks
            .Select(check => DomainGuard.Defined(check, nameof(explicitlySelectedChecks)))
            .ToImmutableHashSet();
    }

    public ReleaseId ReleaseId { get; }

    public int RoundNumber { get; }

    public ImmutableDictionary<ReadinessCheck, DomainBranchResult> PreviousResults { get; }

    public ImmutableDictionary<ReadinessCheck, Guid> CurrentEvidenceIds { get; }

    public ImmutableHashSet<ReadinessCheck> ExplicitlySelectedChecks { get; }

    private static ImmutableDictionary<ReadinessCheck, DomainBranchResult> CopyPreviousResults(
        IEnumerable<DomainBranchResult> previousResults,
        ReleaseId releaseId,
        int roundNumber)
    {
        var results = DomainGuard.Copy(previousResults, nameof(previousResults));
        if (!results.IsEmpty && results.Length != Enum.GetValues<ReadinessCheck>().Length)
        {
            throw new InvalidOperationException(
                "Planning requires either no prior results or one prior result for every readiness check.");
        }

        if (results.Any(result =>
                result.ReleaseId != releaseId
                || result.RoundNumber >= roundNumber))
        {
            throw new InvalidOperationException(
                "Prior results must belong to this release and an earlier round.");
        }

        try
        {
            return results.ToImmutableDictionary(result => result.Check);
        }
        catch (ArgumentException exception)
        {
            throw new InvalidOperationException(
                "Planning requires exactly one prior result for every readiness check.",
                exception);
        }
    }

    private static ImmutableDictionary<ReadinessCheck, Guid> CopyCurrentEvidenceIds(
        IReadOnlyDictionary<ReadinessCheck, Guid> currentEvidenceIds)
    {
        ArgumentNullException.ThrowIfNull(currentEvidenceIds);
        return currentEvidenceIds.ToImmutableDictionary(
            pair => DomainGuard.Defined(pair.Key, nameof(currentEvidenceIds)),
            pair => pair.Value != Guid.Empty
                ? pair.Value
                : throw new ArgumentException(
                    "A current evidence ID cannot be empty.",
                    nameof(currentEvidenceIds)));
    }

}

internal sealed record EvaluationRoundStart
{
    public EvaluationRoundStart(
        Guid roundId,
        int roundNumber,
        UtcInstant startedAt,
        IEnumerable<ReadinessCheck> explicitlySelectedChecks)
    {
        if (roundId == Guid.Empty)
        {
            throw new ArgumentException("An evaluation round ID cannot be empty.", nameof(roundId));
        }

        if (roundNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(roundNumber));
        }

        RoundId = roundId;
        RoundNumber = roundNumber;
        StartedAt = startedAt;
        ExplicitlySelectedChecks = explicitlySelectedChecks
            .Select(check => DomainGuard.Defined(check, nameof(explicitlySelectedChecks)))
            .Distinct()
            .ToImmutableArray();
    }

    public Guid RoundId { get; }

    public int RoundNumber { get; }

    public UtcInstant StartedAt { get; }

    public ImmutableArray<ReadinessCheck> ExplicitlySelectedChecks { get; }
}

internal sealed record RemediationWorkflowResponse
{
    public RemediationWorkflowResponse(
        RemediationSubmission submission,
        IEnumerable<EvidenceRecord> evidenceReplacements)
    {
        Submission = submission ?? throw new ArgumentNullException(nameof(submission));
        EvidenceReplacements = DomainGuard.Copy(
            evidenceReplacements,
            nameof(evidenceReplacements));
    }

    public RemediationSubmission Submission { get; }

    public ImmutableArray<EvidenceRecord> EvidenceReplacements { get; }
}

internal sealed record PlannedBranchWorkItem(
    EvaluationRoundStart Round,
    DomainBranchWorkItem WorkItem,
    Guid ResultId,
    DomainBranchResult? ReuseSource,
    Guid? CurrentEvidenceId);

internal sealed record CompletedBranchWork(
    EvaluationRoundStart Round,
    ReadinessBranch Branch,
    string ExecutorId,
    DomainBranchResult Result);

internal sealed record BuildDecisionSnapshot(EvaluationRound Round);
