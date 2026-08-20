using ReleaseReadinessCoordinator.Data;
using ReleaseReadinessCoordinator.Domain;
using ReleaseReadinessCoordinator.Readiness;

namespace ReleaseReadinessCoordinator.Workflow;

internal sealed class ReleaseWorkflowService
{
    private readonly IApplicationDataService _dataService;
    private readonly CheckpointStoreCoordinator _checkpointCoordinator;
    private readonly TimeProvider _timeProvider;
    private readonly Func<IApplicationDataService, TimeProvider, ReadinessWorkflowDependencies>
        _dependenciesFactory;

    public ReleaseWorkflowService(
        IApplicationDataService dataService,
        CheckpointStoreCoordinator checkpointCoordinator,
        TimeProvider timeProvider)
        : this(dataService, checkpointCoordinator, timeProvider, CreateDefaultDependencies)
    {
    }

    internal ReleaseWorkflowService(
        IApplicationDataService dataService,
        CheckpointStoreCoordinator checkpointCoordinator,
        TimeProvider timeProvider,
        Func<IApplicationDataService, TimeProvider, ReadinessWorkflowDependencies> dependenciesFactory)
    {
        _dataService = dataService ?? throw new ArgumentNullException(nameof(dataService));
        _checkpointCoordinator = checkpointCoordinator
            ?? throw new ArgumentNullException(nameof(checkpointCoordinator));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _dependenciesFactory = dependenciesFactory
            ?? throw new ArgumentNullException(nameof(dependenciesFactory));
    }

    public async Task StartAsync(
        ReleaseSubmission submission,
        EvaluationRoundStart input,
        string sessionId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        await ExecuteTurnAsync(
            submission.ReleaseId,
            token => StartCoreAsync(submission, input, sessionId, token),
            cancellationToken);
    }

    private async Task<PendingWorkflowWait> StartCoreAsync(
        ReleaseSubmission submission,
        EvaluationRoundStart input,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var pendingWait = await _checkpointCoordinator.StartAsync(
            CreateWorkflow(submission),
            input,
            sessionId,
            cancellationToken);
        await SavePendingWaitAsync(
            submission.ReleaseId,
            sessionId,
            pendingWait,
            cancellationToken);
        return pendingWait;
    }

    public async Task<PendingWorkflowWait> RestoreAsync(
        ReleaseId releaseId,
        CancellationToken cancellationToken = default)
    {
        return await ExecuteTurnAsync(
            releaseId,
            token => RestoreCoreAsync(releaseId, token),
            cancellationToken);
    }

    private async Task<PendingWorkflowWait> RestoreCoreAsync(
        ReleaseId releaseId,
        CancellationToken cancellationToken)
    {
        var detail = await GetDetailAsync(releaseId, cancellationToken);
        var expectedWait = WorkflowWaitResolver.Require(detail);
        await _checkpointCoordinator.RestoreAsync(
            CreateWorkflow(detail.Release.Submission),
            detail.WorkflowCorrelation!.WorkflowSessionId,
            expectedWait,
            cancellationToken);
        return expectedWait;
    }

    public async Task ResumeRemediationAsync(
        ReleaseId releaseId,
        RemediationWorkflowResponse response,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        await ExecuteTurnAsync(
            releaseId,
            token => ResumeRemediationCoreAsync(releaseId, response, token),
            cancellationToken);
    }

    private async Task<PendingWorkflowWait> ResumeRemediationCoreAsync(
        ReleaseId releaseId,
        RemediationWorkflowResponse response,
        CancellationToken cancellationToken)
    {
        var detail = await GetDetailAsync(releaseId, cancellationToken);
        if (IsExactRemediationReplay(detail, response))
        {
            return await RestoreCoreAsync(releaseId, cancellationToken);
        }

        var expectedWait = WorkflowWaitResolver.Require(detail) as PendingRemediationWait
            ?? throw new InvalidOperationException("The release is not waiting for remediation.");
        var nextWait = await _checkpointCoordinator.ResumeRemediationAsync(
            CreateWorkflow(detail.Release.Submission),
            detail.WorkflowCorrelation!.WorkflowSessionId,
            expectedWait,
            response,
            cancellationToken);
        await SavePendingWaitAsync(
            releaseId,
            detail.WorkflowCorrelation.WorkflowSessionId,
            nextWait,
            cancellationToken);
        return nextWait;
    }

    public async Task ResumeApprovalAsync(
        ReleaseId releaseId,
        ApprovalResponse response,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(response);
        await ExecuteTurnAsync(
            releaseId,
            token => ResumeApprovalCoreAsync(releaseId, response, token),
            cancellationToken);
    }

    private async Task<PersistedHumanResponse> ResumeApprovalCoreAsync(
        ReleaseId releaseId,
        ApprovalResponse response,
        CancellationToken cancellationToken)
    {
        var detail = await GetDetailAsync(releaseId, cancellationToken);
        var replay = GetExactTerminalReplay(detail, response);
        if (replay is not null)
        {
            return replay;
        }

        var expectedWait = WorkflowWaitResolver.Require(detail) as PendingApprovalWait
            ?? throw new InvalidOperationException("The release is not waiting for approval.");
        try
        {
            return await _checkpointCoordinator.ResumeApprovalAsync(
                CreateWorkflow(detail.Release.Submission),
                detail.WorkflowCorrelation!.WorkflowSessionId,
                expectedWait,
                response,
                cancellationToken);
        }
        catch (WorkflowContinuationException)
        {
            detail = await GetDetailAsync(releaseId, cancellationToken);
            var concurrentReplay = GetExactTerminalReplay(detail, response);
            if (concurrentReplay is not null)
            {
                return concurrentReplay;
            }

            throw;
        }
    }

    private Task<T> ExecuteTurnAsync<T>(
        ReleaseId releaseId,
        Func<CancellationToken, Task<T>> operation,
        CancellationToken cancellationToken) =>
        _checkpointCoordinator.ExecuteExclusivelyAsync(
            token => ExecuteWithFailureAsync(
                releaseId,
                () => operation(token),
                token),
            cancellationToken);

    private async Task<T> ExecuteWithFailureAsync<T>(
        ReleaseId releaseId,
        Func<Task<T>> operation,
        CancellationToken cancellationToken)
    {
        try
        {
            return await operation();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception failure)
        {
            try
            {
                await MarkFailedAsync(
                    releaseId,
                    failure,
                    cancellationToken);
            }
            catch (Exception persistenceFailure)
            {
                throw new AggregateException(
                    "Workflow execution failed and its diagnostic state could not be persisted.",
                    failure,
                    persistenceFailure);
            }

            throw;
        }
    }

    private Microsoft.Agents.AI.Workflows.Workflow CreateWorkflow(ReleaseSubmission submission) =>
        ReleaseWorkflowFactory.Create(
            submission,
            _dataService,
            _timeProvider,
            _dependenciesFactory(_dataService, _timeProvider));

    private static ReadinessWorkflowDependencies CreateDefaultDependencies(
        IApplicationDataService dataService,
        TimeProvider timeProvider) => new(
        new ApplicationDataTestEvidenceProvider(dataService),
        new TestReadinessPolicy(),
        new ApplicationDataSecurityEvidenceProvider(dataService),
        new SecurityReadinessPolicy(),
        new ApplicationDataChangeEvidenceProvider(dataService),
        new ChangeReadinessPolicy());

    private async Task<ReleaseDetailProjection> GetDetailAsync(
        ReleaseId releaseId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(releaseId);
        return await _dataService.GetReleaseDetailAsync(releaseId, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Release '{releaseId}' does not exist.");
    }

    private async Task SavePendingWaitAsync(
        ReleaseId releaseId,
        string sessionId,
        PendingWorkflowWait pendingWait,
        CancellationToken cancellationToken)
    {
        var requestKind = pendingWait switch
        {
            PendingRemediationWait => WorkflowRequestKind.Remediation,
            PendingApprovalWait => WorkflowRequestKind.Approval,
            _ => throw new InvalidOperationException(
                $"Unknown pending workflow wait '{pendingWait.GetType().Name}'."),
        };
        await _dataService.SaveWorkflowCorrelationAsync(
            new WorkflowCorrelationRecord(
                releaseId,
                sessionId,
                pendingWait.WorkflowRequestId,
                pendingWait.DomainRequestId,
                requestKind,
                new UtcInstant(_timeProvider.GetUtcNow())),
            $"workflow-correlation:{sessionId}:{pendingWait.WorkflowRequestId}",
            cancellationToken);
    }

    private async Task MarkFailedAsync(
        ReleaseId releaseId,
        Exception failure,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(failure);
        var detail = await GetDetailAsync(releaseId, cancellationToken);
        if (detail.Release.Phase is ProcessPhase.Failed)
        {
            return;
        }

        var failureKind = failure switch
        {
            _ when ContainsSqliteException(failure) => "SQLite",
            WorkflowContinuationException continuation => continuation.Kind.ToString(),
            InvalidOperationException => "ImpossibleState",
            _ => "Technical",
        };
        var failedAt = new UtcInstant(_timeProvider.GetUtcNow());
        var timelineEntry = new TimelineEntry(
            Guid.NewGuid(),
            releaseId,
            detail.Timeline.IsEmpty ? 1 : detail.Timeline[^1].Sequence + 1,
            TimelineEntryKind.WorkflowFailed,
            $"Workflow failed ({failureKind}): {failure.Message}",
            failedAt);
        await _dataService.MarkWorkflowFailedAsync(
            releaseId,
            failedAt,
            timelineEntry,
            $"workflow-failure:{releaseId.Value}",
            cancellationToken);
    }

    private static bool ContainsSqliteException(Exception exception) =>
        exception.GetType().FullName is "Microsoft.Data.Sqlite.SqliteException"
        || exception.InnerException is not null && ContainsSqliteException(exception.InnerException)
        || exception is AggregateException aggregate
            && aggregate.InnerExceptions.Any(ContainsSqliteException);

    private static PersistedHumanResponse? GetExactTerminalReplay(
        ReleaseDetailProjection detail,
        ApprovalResponse response) =>
        detail.TerminalResponse is { } persisted && persisted.Response == response.Response
            ? persisted
            : null;

    private static bool IsExactRemediationReplay(
        ReleaseDetailProjection detail,
        RemediationWorkflowResponse response)
    {
        var persisted = detail.RemediationSubmissions.SingleOrDefault(
            submission => submission.Id == response.Submission.Id);
        if (persisted is null)
        {
            return false;
        }

        var replacementsMatch = response.EvidenceReplacements.All(replacement =>
            detail.EvidenceHistory.Any(evidence =>
                ApplicationDataService.EvidenceEquals(evidence, replacement)));
        var submissionMatches = persisted.RequestId == response.Submission.RequestId
            && persisted.SubmittedAt == response.Submission.SubmittedAt
            && persisted.EvidenceUpdates.Count == response.Submission.EvidenceUpdates.Count
            && persisted.EvidenceUpdates.All(pair =>
                response.Submission.EvidenceUpdates.TryGetValue(pair.Key, out var evidenceId)
                && evidenceId == pair.Value)
            && persisted.ExplicitlySelectedChecks.SequenceEqual(
                response.Submission.ExplicitlySelectedChecks)
            && response.EvidenceReplacements.Length == persisted.EvidenceUpdates.Count
            && replacementsMatch;
        if (!submissionMatches)
        {
            throw new ApplicationDataConflictException(
                ApplicationDataConflictKind.OperationKeyReused,
                $"Remediation response ID '{response.Submission.Id}' was reused with different content.");
        }

        return true;
    }

}
