using System.Collections.Immutable;
using ReleaseReadinessCoordinator.Domain;

namespace ReleaseReadinessCoordinator.Data;

public sealed record ReleaseDetailProjection(
    ReleaseRevision Release,
    ImmutableArray<EvidenceRecord> EvidenceHistory,
    ImmutableDictionary<EvidenceKind, EvidenceRecord> CurrentEvidence,
    ImmutableArray<EvaluationRound> EvaluationRounds,
    ImmutableArray<RemediationRequest> RemediationRequests,
    ImmutableArray<RemediationSubmission> RemediationSubmissions,
    ImmutableArray<DecisionSnapshot> DecisionSnapshots,
    ImmutableArray<HumanDecisionRequest> HumanDecisionRequests,
    ImmutableArray<PersistedHumanResponse> HumanResponses,
    WorkflowCorrelationRecord? WorkflowCorrelation,
    ImmutableArray<TimelineEntry> Timeline);

public sealed record PersistedHumanResponse(
    ReleaseRevisionKey ReleaseRevision,
    HumanResponse Response,
    HumanResponseValidation Validation);
