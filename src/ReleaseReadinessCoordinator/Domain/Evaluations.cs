using System.Collections.Immutable;

namespace ReleaseReadinessCoordinator.Domain;

public enum ReadinessCheck
{
    Test = 1,
    Security = 2,
    Change = 3,
}

public enum BranchOutcome
{
    Passed = 1,
    Blocked = 2,
    MissingEvidence = 3,
    TransientFailure = 4,
}

public enum WorkDisposition
{
    Execute = 1,
    Reuse = 2,
}

public enum PlanningReason
{
    InitialEvaluation = 1,
    PreviousResultNotPassed = 2,
    EvidenceChanged = 3,
    Expired = 4,
    ExplicitlySelected = 5,
    StillCurrent = 6,
}

public enum ExecutionDisposition
{
    Executed = 1,
    Reused = 2,
}

public sealed record BranchWorkItem
{
    public BranchWorkItem(
        ReleaseId releaseId,
        int roundNumber,
        ReadinessCheck check,
        WorkDisposition disposition,
        PlanningReason planningReason,
        string planningDetail,
        Guid? reuseSourceResultId = null)
    {
        ValidateRound(roundNumber);
        DomainGuard.Defined(check, nameof(check));
        DomainGuard.Defined(disposition, nameof(disposition));
        DomainGuard.Defined(planningReason, nameof(planningReason));

        if (disposition is WorkDisposition.Execute
            && (planningReason is PlanningReason.StillCurrent || reuseSourceResultId.HasValue))
        {
            throw new ArgumentException(
                "Execute work requires an execution reason and cannot name a reuse source.",
                nameof(planningReason));
        }

        if (disposition is WorkDisposition.Reuse
            && (planningReason is not PlanningReason.StillCurrent
                || !reuseSourceResultId.HasValue
                || reuseSourceResultId.Value == Guid.Empty))
        {
            throw new ArgumentException(
                "Reuse work must be still current and name a source result.",
                nameof(planningReason));
        }

        ReleaseId = releaseId;
        RoundNumber = roundNumber;
        Check = check;
        Disposition = disposition;
        PlanningReason = planningReason;
        PlanningDetail = DomainGuard.Required(planningDetail, nameof(planningDetail));
        ReuseSourceResultId = reuseSourceResultId;
    }

    public ReleaseId ReleaseId { get; }

    public int RoundNumber { get; }

    public ReadinessCheck Check { get; }

    public WorkDisposition Disposition { get; }

    public PlanningReason PlanningReason { get; }

    public string PlanningDetail { get; }

    public Guid? ReuseSourceResultId { get; }

    private static void ValidateRound(int roundNumber)
    {
        if (roundNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(roundNumber), "A round number must be positive.");
        }
    }
}

public sealed record BranchResult
{
    public BranchResult(
        Guid id,
        ReleaseId releaseId,
        int roundNumber,
        ReadinessCheck check,
        BranchOutcome outcome,
        ExecutionDisposition disposition,
        PlanningReason planningReason,
        string planningDetail,
        Guid? evidenceId,
        EvidenceKind evidenceKind,
        UtcInstant? validUntil,
        IEnumerable<string> attempts,
        IReadOnlyDictionary<string, string> findings,
        Guid? reuseSourceResultId,
        int? reuseSourceRound)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A result ID cannot be empty.", nameof(id));
        }

        if (evidenceId == Guid.Empty)
        {
            throw new ArgumentException("An evidence ID cannot be empty.", nameof(evidenceId));
        }

        if (roundNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(roundNumber), "A round number must be positive.");
        }

        DomainGuard.Defined(check, nameof(check));
        DomainGuard.Defined(outcome, nameof(outcome));
        DomainGuard.Defined(disposition, nameof(disposition));
        DomainGuard.Defined(planningReason, nameof(planningReason));
        if (EvidenceKindFor(check) != evidenceKind)
        {
            throw new ArgumentException($"The {check} result cannot use {evidenceKind} evidence.", nameof(evidenceKind));
        }

        if (outcome is not BranchOutcome.MissingEvidence && !evidenceId.HasValue)
        {
            throw new ArgumentException("This outcome requires an evidence record.", nameof(evidenceId));
        }

        if (outcome is BranchOutcome.Passed && !validUntil.HasValue)
        {
            throw new ArgumentException("A passing result requires a validity deadline.", nameof(validUntil));
        }

        if (outcome is not BranchOutcome.Passed && validUntil.HasValue)
        {
            throw new ArgumentException("A non-passing result cannot carry a validity deadline.", nameof(validUntil));
        }

        ValidatePlanning(disposition, planningReason);
        ValidateReuse(roundNumber, outcome, disposition, reuseSourceResultId, reuseSourceRound);

        Id = id;
        ReleaseId = releaseId;
        RoundNumber = roundNumber;
        Check = check;
        Outcome = outcome;
        Disposition = disposition;
        PlanningReason = planningReason;
        PlanningDetail = DomainGuard.Required(planningDetail, nameof(planningDetail));
        EvidenceId = evidenceId;
        EvidenceKind = evidenceKind;
        ValidUntil = validUntil;
        Attempts = DomainGuard.Copy(
            attempts.Select(value => DomainGuard.Required(value, nameof(attempts))),
            nameof(attempts));
        Findings = findings.ToImmutableDictionary(
            pair => DomainGuard.Required(pair.Key, nameof(findings)),
            pair => DomainGuard.Required(pair.Value, nameof(findings)),
            StringComparer.Ordinal);
        ReuseSourceResultId = reuseSourceResultId;
        ReuseSourceRound = reuseSourceRound;
    }

    public Guid Id { get; }

    public ReleaseId ReleaseId { get; }

    public int RoundNumber { get; }

    public ReadinessCheck Check { get; }

    public BranchOutcome Outcome { get; }

    public ExecutionDisposition Disposition { get; }

    public PlanningReason PlanningReason { get; }

    public string PlanningDetail { get; }

    public Guid? EvidenceId { get; }

    public EvidenceKind EvidenceKind { get; }

    public UtcInstant? ValidUntil { get; }

    public ImmutableArray<string> Attempts { get; }

    public ImmutableDictionary<string, string> Findings { get; }

    public Guid? ReuseSourceResultId { get; }

    public int? ReuseSourceRound { get; }

    private static void ValidatePlanning(
        ExecutionDisposition disposition,
        PlanningReason planningReason)
    {
        if (disposition is ExecutionDisposition.Executed
            && planningReason is PlanningReason.StillCurrent)
        {
            throw new ArgumentException(
                "An executed result requires an execution reason.",
                nameof(planningReason));
        }

        if (disposition is ExecutionDisposition.Reused
            && planningReason is not PlanningReason.StillCurrent)
        {
            throw new ArgumentException(
                "A reused result must use the still-current reason.",
                nameof(planningReason));
        }
    }

    private static void ValidateReuse(
        int roundNumber,
        BranchOutcome outcome,
        ExecutionDisposition disposition,
        Guid? sourceResultId,
        int? sourceRound)
    {
        if (disposition is ExecutionDisposition.Executed)
        {
            if (sourceResultId.HasValue || sourceRound.HasValue)
            {
                throw new ArgumentException("An executed result cannot name a reuse source.", nameof(sourceResultId));
            }

            return;
        }

        if (outcome is not BranchOutcome.Passed
            || !sourceResultId.HasValue
            || sourceResultId.Value == Guid.Empty
            || sourceRound is null or <= 0
            || sourceRound >= roundNumber)
        {
            throw new ArgumentException(
                "A reused result must pass and name a source from an earlier round.",
                nameof(sourceResultId));
        }
    }

    private static EvidenceKind EvidenceKindFor(ReadinessCheck check) => check switch
    {
        ReadinessCheck.Test => EvidenceKind.Test,
        ReadinessCheck.Security => EvidenceKind.Security,
        ReadinessCheck.Change => EvidenceKind.Change,
        _ => throw new ArgumentOutOfRangeException(nameof(check)),
    };
}

public sealed record EvaluationRound
{
    public EvaluationRound(
        Guid id,
        ReleaseId releaseId,
        int roundNumber,
        UtcInstant startedAt,
        UtcInstant completedAt,
        IEnumerable<BranchResult> results)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("An evaluation round ID cannot be empty.", nameof(id));
        }

        if (roundNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(roundNumber), "A round number must be positive.");
        }

        if (completedAt < startedAt)
        {
            throw new ArgumentException("A round cannot complete before it starts.", nameof(completedAt));
        }

        var completeResults = RequireEveryCheck(results, "An evaluation round");
        if (completeResults.Any(result => result.ReleaseId != releaseId || result.RoundNumber != roundNumber))
        {
            throw new InvalidOperationException("Every result must belong to the evaluation round and release.");
        }

        Id = id;
        ReleaseId = releaseId;
        RoundNumber = roundNumber;
        StartedAt = startedAt;
        CompletedAt = completedAt;
        Results = completeResults;
    }

    public Guid Id { get; }

    public ReleaseId ReleaseId { get; }

    public int RoundNumber { get; }

    public UtcInstant StartedAt { get; }

    public UtcInstant CompletedAt { get; }

    public ImmutableArray<BranchResult> Results { get; }

    internal static ImmutableArray<BranchResult> RequireEveryCheck(
        IEnumerable<BranchResult> results,
        string subject)
    {
        var ordered = DomainGuard.Copy(results, nameof(results)).OrderBy(result => result.Check).ToImmutableArray();
        if (ordered.Length != Enum.GetValues<ReadinessCheck>().Length
            || ordered.Select(result => result.Check).Distinct().Count() != ordered.Length)
        {
            throw new InvalidOperationException($"{subject} requires exactly one result for each readiness check.");
        }

        return ordered;
    }
}

public sealed record RemediationRequest
{
    public RemediationRequest(
        Guid id,
        ReleaseId releaseId,
        int roundNumber,
        UtcInstant createdAt,
        IEnumerable<BranchResult> problems)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A remediation request ID cannot be empty.", nameof(id));
        }

        if (roundNumber <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(roundNumber));
        }

        var currentProblems = DomainGuard.Copy(problems, nameof(problems));
        if (currentProblems.IsEmpty
            || currentProblems.Any(result => result.Outcome is BranchOutcome.Passed))
        {
            throw new ArgumentException("Remediation requires at least one non-passing result.", nameof(problems));
        }

        if (currentProblems.Select(result => result.Check).Distinct().Count() != currentProblems.Length)
        {
            throw new InvalidOperationException("A remediation request cannot repeat a readiness check.");
        }

        if (currentProblems.Any(result => result.ReleaseId != releaseId || result.RoundNumber != roundNumber))
        {
            throw new InvalidOperationException("Every problem must belong to the request round and release.");
        }

        Id = id;
        ReleaseId = releaseId;
        RoundNumber = roundNumber;
        CreatedAt = createdAt;
        Problems = [.. currentProblems.OrderBy(result => result.Check)];
    }

    public Guid Id { get; }

    public ReleaseId ReleaseId { get; }

    public int RoundNumber { get; }

    public UtcInstant CreatedAt { get; }

    public ImmutableArray<BranchResult> Problems { get; }
}

public sealed record RemediationSubmission
{
    public RemediationSubmission(
        Guid id,
        Guid requestId,
        UtcInstant submittedAt,
        IReadOnlyDictionary<EvidenceKind, Guid> evidenceUpdates,
        IEnumerable<ReadinessCheck> explicitlySelectedChecks)
    {
        if (id == Guid.Empty || requestId == Guid.Empty)
        {
            throw new ArgumentException("Remediation submission and request IDs cannot be empty.");
        }

        EvidenceUpdates = evidenceUpdates.ToImmutableDictionary(
            pair => DomainGuard.Defined(pair.Key, nameof(evidenceUpdates)),
            pair => pair.Value != Guid.Empty
                ? pair.Value
                : throw new ArgumentException("An evidence update ID cannot be empty.", nameof(evidenceUpdates)));
        ExplicitlySelectedChecks = DomainGuard.Copy(explicitlySelectedChecks, nameof(explicitlySelectedChecks))
            .Select(check => DomainGuard.Defined(check, nameof(explicitlySelectedChecks)))
            .Distinct()
            .ToImmutableArray();
        Id = id;
        RequestId = requestId;
        SubmittedAt = submittedAt;
    }

    public Guid Id { get; }

    public Guid RequestId { get; }

    public UtcInstant SubmittedAt { get; }

    public ImmutableDictionary<EvidenceKind, Guid> EvidenceUpdates { get; }

    public ImmutableArray<ReadinessCheck> ExplicitlySelectedChecks { get; }
}
