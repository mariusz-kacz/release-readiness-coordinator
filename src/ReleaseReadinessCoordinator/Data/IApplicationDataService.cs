using ReleaseReadinessCoordinator.Domain;

namespace ReleaseReadinessCoordinator.Data;

public interface IApplicationDataService
{
    Task<EvidenceRecord?> GetCurrentEvidenceAsync(
        ReleaseRevisionKey releaseRevision,
        EvidenceKind kind,
        CancellationToken cancellationToken = default);

    Task<ReleaseRevision> SubmitReleaseAsync(
        ReleaseSubmission submission,
        IReadOnlyCollection<EvidenceRecord> initialEvidence,
        TimelineEntry timelineEntry,
        string operationKey,
        CancellationToken cancellationToken = default);

    Task<EvidenceRecord> ReplaceEvidenceAsync(
        EvidenceRecord evidence,
        TimelineEntry timelineEntry,
        string operationKey,
        CancellationToken cancellationToken = default);

    Task<EvaluationRound> SaveEvaluationRoundAsync(
        EvaluationRound round,
        TimelineEntry timelineEntry,
        string operationKey,
        CancellationToken cancellationToken = default);

    Task<RemediationRequest> OpenRemediationRequestAsync(
        RemediationRequest request,
        TimelineEntry timelineEntry,
        string operationKey,
        CancellationToken cancellationToken = default);

    Task<RemediationSubmission> SaveRemediationSubmissionAsync(
        ReleaseRevisionKey releaseRevision,
        RemediationSubmission submission,
        IReadOnlyCollection<EvidenceRecord> evidenceReplacements,
        TimelineEntry timelineEntry,
        string operationKey,
        CancellationToken cancellationToken = default);

    Task<HumanDecisionRequest> OpenHumanDecisionRequestAsync(
        DecisionSnapshot snapshot,
        HumanDecisionRequest request,
        TimelineEntry timelineEntry,
        string operationKey,
        CancellationToken cancellationToken = default);

    Task<PersistedHumanResponse> SaveHumanResponseAsync(
        ReleaseRevisionKey releaseRevision,
        Guid approvalRequestId,
        HumanResponse response,
        TimelineEntry timelineEntry,
        string operationKey,
        CancellationToken cancellationToken = default);

    Task<WorkflowCorrelationRecord> SaveWorkflowCorrelationAsync(
        WorkflowCorrelationRecord correlation,
        string operationKey,
        CancellationToken cancellationToken = default);

    Task<ReleaseRevision> MarkWorkflowFailedAsync(
        ReleaseRevisionKey releaseRevision,
        UtcInstant failedAt,
        TimelineEntry timelineEntry,
        string operationKey,
        CancellationToken cancellationToken = default);

    Task<TimelineEntry> AppendTimelineEntryAsync(
        TimelineEntry timelineEntry,
        string operationKey,
        CancellationToken cancellationToken = default);

    Task<ReleaseDetailProjection?> GetReleaseDetailAsync(
        ReleaseRevisionKey releaseRevision,
        CancellationToken cancellationToken = default);
}

public enum ApplicationDataConflictKind
{
    DuplicateReleaseRevision = 1,
    OperationKeyReused = 2,
    TerminalRelease = 3,
    InvalidState = 5,
}

public sealed class ApplicationDataConflictException : InvalidOperationException
{
    public ApplicationDataConflictException(ApplicationDataConflictKind kind, string message)
        : base(message)
    {
        Kind = kind;
    }

    public ApplicationDataConflictKind Kind { get; }
}
