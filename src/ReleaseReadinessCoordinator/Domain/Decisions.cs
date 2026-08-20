using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace ReleaseReadinessCoordinator.Domain;

public sealed record DecisionSnapshot
{
    [JsonConstructor]
    private DecisionSnapshot(
        Guid id,
        ReleaseId releaseId,
        Guid evaluationRoundId,
        int roundNumber,
        ImmutableArray<BranchResult> sources,
        UtcInstant earliestValidityBound,
        string decisionBrief,
        UtcInstant createdAt)
        : this(
            id,
            releaseId,
            evaluationRoundId,
            roundNumber,
            (IEnumerable<BranchResult>)sources,
            earliestValidityBound,
            decisionBrief,
            createdAt)
    {
    }

    public DecisionSnapshot(
        Guid id,
        ReleaseId releaseId,
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
                result.ReleaseId != releaseId
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
        ReleaseId = releaseId;
        EvaluationRoundId = evaluationRoundId;
        RoundNumber = roundNumber;
        Sources = completeSources;
        EarliestValidityBound = earliestValidityBound;
        DecisionBrief = DomainGuard.Required(decisionBrief, nameof(decisionBrief));
        CreatedAt = createdAt;
    }

    public Guid Id { get; }

    public ReleaseId ReleaseId { get; }

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
        HumanDecision decision,
        string responder,
        string comment,
        UtcInstant respondedAt)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A response ID cannot be empty.", nameof(id));
        }

        Id = id;
        Decision = DomainGuard.Defined(decision, nameof(decision));
        Responder = DomainGuard.Required(responder, nameof(responder));
        Comment = DomainGuard.Required(comment, nameof(comment));
        RespondedAt = respondedAt;
    }

    public Guid Id { get; }

    public HumanDecision Decision { get; }

    public string Responder { get; }

    public string Comment { get; }

    public UtcInstant RespondedAt { get; }
}

public sealed record HumanDecisionRequest
{
    public HumanDecisionRequest(
        Guid id,
        ReleaseId releaseId,
        Guid snapshotId,
        UtcInstant createdAt)
    {
        if (id == Guid.Empty || snapshotId == Guid.Empty)
        {
            throw new ArgumentException("Request and snapshot IDs cannot be empty.");
        }

        Id = id;
        ReleaseId = releaseId;
        SnapshotId = snapshotId;
        CreatedAt = createdAt;
    }

    public Guid Id { get; }

    public ReleaseId ReleaseId { get; }

    public Guid SnapshotId { get; }

    public UtcInstant CreatedAt { get; }
}

public enum WorkflowRequestKind
{
    Remediation = 1,
    Approval = 2,
}

public sealed record WorkflowCorrelationRecord
{
    public WorkflowCorrelationRecord(
        ReleaseId releaseId,
        string workflowSessionId,
        string pendingWorkflowRequestId,
        Guid pendingDomainRequestId,
        WorkflowRequestKind pendingRequestKind,
        UtcInstant correlatedAt)
    {
        ReleaseId = releaseId;
        WorkflowSessionId = DomainGuard.Required(workflowSessionId, nameof(workflowSessionId));
        PendingWorkflowRequestId = DomainGuard.Required(pendingWorkflowRequestId, nameof(pendingWorkflowRequestId));
        if (pendingDomainRequestId == Guid.Empty)
        {
            throw new ArgumentException(
                "A pending domain request ID cannot be empty.",
                nameof(pendingDomainRequestId));
        }

        PendingDomainRequestId = pendingDomainRequestId;
        PendingRequestKind = DomainGuard.Defined(pendingRequestKind, nameof(pendingRequestKind));
        CorrelatedAt = correlatedAt;
    }

    public ReleaseId ReleaseId { get; }

    public string WorkflowSessionId { get; }

    public string PendingWorkflowRequestId { get; }

    public Guid PendingDomainRequestId { get; }

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
    WorkflowFailed = 8,
}

public sealed record TimelineEntry
{
    public TimelineEntry(
        Guid id,
        ReleaseId releaseId,
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
        ReleaseId = releaseId;
        Sequence = sequence;
        Kind = DomainGuard.Defined(kind, nameof(kind));
        Summary = DomainGuard.Required(summary, nameof(summary));
        OccurredAt = occurredAt;
    }

    public Guid Id { get; }

    public ReleaseId ReleaseId { get; }

    public long Sequence { get; }

    public TimelineEntryKind Kind { get; }

    public string Summary { get; }

    public UtcInstant OccurredAt { get; }
}
