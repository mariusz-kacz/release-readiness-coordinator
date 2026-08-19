using System.Collections.Immutable;
using ReleaseReadinessCoordinator.Domain;

namespace ReleaseReadinessCoordinator.Data;

public sealed record ReleaseDetailProjection(
    Release Release,
    ImmutableArray<EvidenceRecord> EvidenceHistory,
    ImmutableDictionary<EvidenceKind, EvidenceRecord> CurrentEvidence,
    ImmutableArray<EvaluationRound> EvaluationRounds,
    ImmutableArray<RemediationRequest> RemediationRequests,
    ImmutableArray<RemediationSubmission> RemediationSubmissions,
    ImmutableArray<DecisionSnapshot> DecisionSnapshots,
    ImmutableArray<HumanDecisionRequest> HumanDecisionRequests,
    PersistedHumanResponse? TerminalResponse,
    WorkflowCorrelationRecord? WorkflowCorrelation,
    ImmutableArray<TimelineEntry> Timeline);

public sealed record PersistedHumanResponse(
    ReleaseId ReleaseId,
    Guid ApprovalRequestId,
    HumanResponse Response);
