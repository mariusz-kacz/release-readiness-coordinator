using System.Collections.Immutable;

namespace ReleaseReadinessCoordinator.Domain;

public sealed record DecisionSnapshot
{
    public DecisionSnapshot(
        Guid id,
        ReleaseRevisionKey releaseRevision,
        Guid evaluationRoundId,
        int roundNumber,
        IEnumerable<BranchResult> sources,
        UtcInstant earliestValidityBound,
        string decisionBrief,
        UtcInstant createdAt)
    {
        if (id == Guid.Empty || evaluationRoundId == Guid.Empty)
        {
            throw new ArgumentException("Snapshot and evaluation round IDs cannot be empty.");
        }

        var completeSources = EvaluationRound.RequireEveryCheck(sources, "A decision snapshot");
        if (completeSources.Any(result =>
                result.ReleaseRevision != releaseRevision
                || result.RoundNumber != roundNumber
                || result.Outcome is not BranchOutcome.Passed))
        {
            throw new InvalidOperationException(
                "Every snapshot source must be a passing result from the selected release round.");
        }

        if (completeSources.Min(result => result.ValidUntil!.Value) != earliestValidityBound)
        {
            throw new ArgumentException("The snapshot validity bound must be the earliest result bound.", nameof(earliestValidityBound));
        }

        Id = id;
        ReleaseRevision = releaseRevision;
        EvaluationRoundId = evaluationRoundId;
        RoundNumber = roundNumber;
        Sources = completeSources;
        EarliestValidityBound = earliestValidityBound;
        DecisionBrief = DomainGuard.Required(decisionBrief, nameof(decisionBrief));
        CreatedAt = createdAt;
    }

    public Guid Id { get; }

    public ReleaseRevisionKey ReleaseRevision { get; }

    public Guid EvaluationRoundId { get; }

    public int RoundNumber { get; }

    public ImmutableArray<BranchResult> Sources { get; }

    public UtcInstant EarliestValidityBound { get; }

    public string DecisionBrief { get; }

    public UtcInstant CreatedAt { get; }
}

public enum HumanDecision
{
    Approve = 1,
    Reject = 2,
}

public sealed record HumanResponse
{
    public HumanResponse(
        Guid id,
        Guid requestId,
        Guid snapshotId,
        string snapshotConcurrencyToken,
        HumanDecision decision,
        string responder,
        UtcInstant respondedAt)
    {
        if (id == Guid.Empty || requestId == Guid.Empty || snapshotId == Guid.Empty)
        {
            throw new ArgumentException("Response, request, and snapshot IDs cannot be empty.");
        }

        Id = id;
        RequestId = requestId;
        SnapshotId = snapshotId;
        SnapshotConcurrencyToken = DomainGuard.Required(snapshotConcurrencyToken, nameof(snapshotConcurrencyToken));
        Decision = DomainGuard.Defined(decision, nameof(decision));
        Responder = DomainGuard.Required(responder, nameof(responder));
        RespondedAt = respondedAt;
    }

    public Guid Id { get; }

    public Guid RequestId { get; }

    public Guid SnapshotId { get; }

    public string SnapshotConcurrencyToken { get; }

    public HumanDecision Decision { get; }

    public string Responder { get; }

    public UtcInstant RespondedAt { get; }
}

public sealed record HumanDecisionRequest
{
    public HumanDecisionRequest(
        Guid id,
        ReleaseRevisionKey releaseRevision,
        Guid snapshotId,
        string concurrencyToken,
        UtcInstant createdAt)
    {
        if (id == Guid.Empty || snapshotId == Guid.Empty)
        {
            throw new ArgumentException("Request and snapshot IDs cannot be empty.");
        }

        Id = id;
        ReleaseRevision = releaseRevision;
        SnapshotId = snapshotId;
        ConcurrencyToken = DomainGuard.Required(concurrencyToken, nameof(concurrencyToken));
        CreatedAt = createdAt;
    }

    public Guid Id { get; }

    public ReleaseRevisionKey ReleaseRevision { get; }

    public Guid SnapshotId { get; }

    public string ConcurrencyToken { get; }

    public UtcInstant CreatedAt { get; }

    public void EnsureCorrelated(HumanResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.RequestId != Id
            || response.SnapshotId != SnapshotId
            || !string.Equals(response.SnapshotConcurrencyToken, ConcurrencyToken, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The response does not match the active request, snapshot, and concurrency token.");
        }
    }
}

public enum HumanResponseValidationState
{
    Accepted = 1,
    Declined = 2,
}

public enum HumanResponseDeclineReason
{
    ProcessPhaseChanged = 1,
    RequestMismatch = 2,
    SnapshotMismatch = 3,
    ConcurrencyTokenChanged = 4,
    EvidenceChanged = 5,
    ResultExpired = 6,
}

public sealed record HumanResponseValidation
{
    private HumanResponseValidation(
        Guid responseId,
        UtcInstant validatedAt,
        HumanResponseValidationState state,
        IEnumerable<HumanResponseDeclineReason> reasons)
    {
        if (responseId == Guid.Empty)
        {
            throw new ArgumentException("A response ID cannot be empty.", nameof(responseId));
        }

        ResponseId = responseId;
        ValidatedAt = validatedAt;
        State = state;
        DeclineReasons = [.. reasons.Distinct()];
    }

    public Guid ResponseId { get; }

    public UtcInstant ValidatedAt { get; }

    public HumanResponseValidationState State { get; }

    public ImmutableArray<HumanResponseDeclineReason> DeclineReasons { get; }

    public static HumanResponseValidation Accepted(Guid responseId, UtcInstant validatedAt) =>
        new(responseId, validatedAt, HumanResponseValidationState.Accepted, []);

    public static HumanResponseValidation Declined(
        Guid responseId,
        UtcInstant validatedAt,
        IEnumerable<HumanResponseDeclineReason> reasons)
    {
        var validatedReasons = DomainGuard.Copy(reasons, nameof(reasons));
        if (validatedReasons.IsEmpty)
        {
            throw new ArgumentException("A declined response requires at least one reason.", nameof(reasons));
        }

        foreach (var reason in validatedReasons)
        {
            DomainGuard.Defined(reason, nameof(reasons));
        }

        return new HumanResponseValidation(
            responseId, validatedAt, HumanResponseValidationState.Declined, validatedReasons);
    }
}

public enum WorkflowRequestKind
{
    Remediation = 1,
    Approval = 2,
}

public sealed record WorkflowCorrelationRecord
{
    public WorkflowCorrelationRecord(
        ReleaseRevisionKey releaseRevision,
        string workflowSessionId,
        string pendingWorkflowRequestId,
        WorkflowRequestKind pendingRequestKind,
        UtcInstant correlatedAt)
    {
        ReleaseRevision = releaseRevision;
        WorkflowSessionId = DomainGuard.Required(workflowSessionId, nameof(workflowSessionId));
        PendingWorkflowRequestId = DomainGuard.Required(pendingWorkflowRequestId, nameof(pendingWorkflowRequestId));
        PendingRequestKind = DomainGuard.Defined(pendingRequestKind, nameof(pendingRequestKind));
        CorrelatedAt = correlatedAt;
    }

    public ReleaseRevisionKey ReleaseRevision { get; }

    public string WorkflowSessionId { get; }

    public string PendingWorkflowRequestId { get; }

    public WorkflowRequestKind PendingRequestKind { get; }

    public UtcInstant CorrelatedAt { get; }
}

public enum TimelineEntryKind
{
    ReleaseSubmitted = 1,
    EvaluationStarted = 2,
    EvaluationCompleted = 3,
    RemediationRequested = 4,
    RemediationSubmitted = 5,
    ApprovalRequested = 6,
    HumanResponseAccepted = 7,
    HumanResponseDeclined = 8,
    WorkflowFailed = 9,
}

public sealed record TimelineEntry
{
    public TimelineEntry(
        Guid id,
        ReleaseRevisionKey releaseRevision,
        long sequence,
        TimelineEntryKind kind,
        string summary,
        UtcInstant occurredAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A timeline entry ID cannot be empty.", nameof(id));
        }

        if (sequence <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sequence), "A timeline sequence must be positive.");
        }

        Id = id;
        ReleaseRevision = releaseRevision;
        Sequence = sequence;
        Kind = DomainGuard.Defined(kind, nameof(kind));
        Summary = DomainGuard.Required(summary, nameof(summary));
        OccurredAt = occurredAt;
    }

    public Guid Id { get; }

    public ReleaseRevisionKey ReleaseRevision { get; }

    public long Sequence { get; }

    public TimelineEntryKind Kind { get; }

    public string Summary { get; }

    public UtcInstant OccurredAt { get; }
}
