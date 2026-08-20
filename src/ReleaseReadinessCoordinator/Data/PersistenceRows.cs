using ReleaseReadinessCoordinator.Domain;

namespace ReleaseReadinessCoordinator.Data;

internal interface IOperationRow
{
    string OperationKey { get; set; }
}

internal sealed class ReleaseRow : IOperationRow
{
    public string ReleaseId { get; set; } = null!;
    public string ServiceName { get; set; } = null!;
    public string ReleaseVersion { get; set; } = null!;
    public DateTimeOffset RequestedWindowStartUtc { get; set; }
    public DateTimeOffset RequestedWindowEndUtc { get; set; }
    public DateTimeOffset SubmittedAtUtc { get; set; }
    public ProcessPhase Phase { get; set; }
    public DateTimeOffset PhaseChangedAtUtc { get; set; }
    public string OperationKey { get; set; } = null!;
    public string ConcurrencyToken { get; set; } = null!;
}

internal sealed class EvidenceRecordRow : IOperationRow
{
    public Guid Id { get; set; }
    public string ReleaseId { get; set; } = null!;
    public EvidenceKind Kind { get; set; }
    public int Version { get; set; }
    public DateTimeOffset RecordedAtUtc { get; set; }
    public Guid? SupersedesEvidenceId { get; set; }
    public string PayloadJson { get; set; } = null!;
    public string OperationKey { get; set; } = null!;
}

internal sealed class CurrentEvidenceRow
{
    public string ReleaseId { get; set; } = null!;
    public EvidenceKind Kind { get; set; }
    public Guid EvidenceId { get; set; }
    public DateTimeOffset SelectedAtUtc { get; set; }
    public string ConcurrencyToken { get; set; } = null!;
}

internal sealed class EvaluationRoundRow : IOperationRow
{
    public Guid Id { get; set; }
    public string ReleaseId { get; set; } = null!;
    public int RoundNumber { get; set; }
    public DateTimeOffset StartedAtUtc { get; set; }
    public DateTimeOffset? CompletedAtUtc { get; set; }
    public string OperationKey { get; set; } = null!;
}

internal sealed class BranchResultRow : IOperationRow
{
    public Guid Id { get; set; }
    public Guid EvaluationRoundId { get; set; }
    public ReadinessCheck Check { get; set; }
    public BranchOutcome Outcome { get; set; }
    public ExecutionDisposition Disposition { get; set; }
    public PlanningReason PlanningReason { get; set; }
    public string PlanningDetail { get; set; } = null!;
    public Guid? EvidenceId { get; set; }
    public EvidenceKind EvidenceKind { get; set; }
    public string AttemptsJson { get; set; } = null!;
    public string FindingsJson { get; set; } = null!;
    public Guid? ReuseSourceResultId { get; set; }
    public int? ReuseSourceRound { get; set; }
    public string OperationKey { get; set; } = null!;
}

internal sealed class WorkflowRequestRow : IOperationRow
{
    public Guid Id { get; set; }
    public string ReleaseId { get; set; } = null!;
    public WorkflowRequestKind Kind { get; set; }
    public Guid? EvaluationRoundId { get; set; }
    public Guid? DecisionSnapshotId { get; set; }
    public DateTimeOffset CreatedAtUtc { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset? ClosedAtUtc { get; set; }
    public string ConcurrencyToken { get; set; } = null!;
    public string OperationKey { get; set; } = null!;
}

internal sealed class RemediationSubmissionRow : IOperationRow
{
    public Guid Id { get; set; }
    public Guid RequestId { get; set; }
    public DateTimeOffset SubmittedAtUtc { get; set; }
    public string EvidenceUpdatesJson { get; set; } = null!;
    public string ExplicitlySelectedChecksJson { get; set; } = null!;
    public string OperationKey { get; set; } = null!;
}

internal sealed class DecisionSnapshotRow : IOperationRow
{
    public Guid Id { get; set; }
    public string ReleaseId { get; set; } = null!;
    public Guid EvaluationRoundId { get; set; }
    public int RoundNumber { get; set; }
    public string DecisionBrief { get; set; } = null!;
    public DateTimeOffset CreatedAtUtc { get; set; }
    public string OperationKey { get; set; } = null!;
}

internal sealed class DecisionSnapshotSourceRow
{
    public Guid DecisionSnapshotId { get; set; }
    public ReadinessCheck Check { get; set; }
    public Guid BranchResultId { get; set; }
    public Guid EvidenceId { get; set; }
}

internal sealed class HumanResponseRow : IOperationRow
{
    public Guid Id { get; set; }
    public string ReleaseId { get; set; } = null!;
    public Guid ApprovalRequestId { get; set; }
    public HumanDecision Decision { get; set; }
    public string Responder { get; set; } = null!;
    public string Comment { get; set; } = null!;
    public DateTimeOffset RespondedAtUtc { get; set; }
    public string OperationKey { get; set; } = null!;
}

internal sealed class WorkflowCorrelationRow : IOperationRow
{
    public string ReleaseId { get; set; } = null!;
    public string WorkflowSessionId { get; set; } = null!;
    public string PendingWorkflowRequestId { get; set; } = null!;
    public Guid PendingDomainRequestId { get; set; }
    public WorkflowRequestKind PendingRequestKind { get; set; }
    public DateTimeOffset CorrelatedAtUtc { get; set; }
    public string ConcurrencyToken { get; set; } = null!;
    public string OperationKey { get; set; } = null!;
}

internal sealed class TimelineEntryRow : IOperationRow
{
    public Guid Id { get; set; }
    public string ReleaseId { get; set; } = null!;
    public long Sequence { get; set; }
    public TimelineEntryKind Kind { get; set; }
    public string Summary { get; set; } = null!;
    public DateTimeOffset OccurredAtUtc { get; set; }
    public string OperationKey { get; set; } = null!;
}
