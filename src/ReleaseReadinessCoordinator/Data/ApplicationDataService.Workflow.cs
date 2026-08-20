using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ReleaseReadinessCoordinator.Domain;

namespace ReleaseReadinessCoordinator.Data;

public sealed partial class ApplicationDataService
{
    public Task<EvaluationRound> SaveEvaluationRoundAsync(
        EvaluationRound round,
        TimelineEntry timelineEntry,
        string operationKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(round);
        ArgumentNullException.ThrowIfNull(timelineEntry);
        operationKey = RequireOperationKey(operationKey);
        ValidateTimeline(timelineEntry, round.ReleaseId, TimelineEntryKind.EvaluationCompleted);

        return ExecuteReplaySafeAsync(
            async token =>
            {
                var row = await _dbContext.EvaluationRounds.AsNoTracking().SingleOrDefaultAsync(
                    value => value.ReleaseId == round.ReleaseId.Value
                        && value.RoundNumber == round.RoundNumber,
                    token);
                if (row is null)
                {
                    await EnsureOperationKeyIsAvailable(
                        _dbContext.EvaluationRounds,
                        operationKey,
                        "evaluation round",
                        token);
                    return null;
                }

                return await ReadEvaluationRound(row, token);
            },
            async token =>
            {
                await RequireReleaseInPhase(round.ReleaseId, ProcessPhase.Evaluating, token);
                _dbContext.EvaluationRounds.Add(new EvaluationRoundRow
                {
                    Id = round.Id,
                    ReleaseId = round.ReleaseId.Value,
                    RoundNumber = round.RoundNumber,
                    StartedAtUtc = round.StartedAt.Value,
                    CompletedAtUtc = round.CompletedAt.Value,
                    OperationKey = operationKey,
                });
                foreach (var result in round.Results)
                {
                    _dbContext.BranchResults.Add(ToRow(result, round.Id, $"{operationKey}:result:{result.Check}"));
                }

                _dbContext.TimelineEntries.Add(ToRow(timelineEntry, TimelineOperationKey(operationKey)));
                return round;
            },
            cancellationToken);
    }

    public Task<RemediationRequest> OpenRemediationRequestAsync(
        RemediationRequest request,
        TimelineEntry timelineEntry,
        string operationKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(timelineEntry);
        operationKey = RequireOperationKey(operationKey);
        ValidateTimeline(timelineEntry, request.ReleaseId, TimelineEntryKind.RemediationRequested);

        return ExecuteReplaySafeAsync(
            async token =>
            {
                var round = await _dbContext.EvaluationRounds.AsNoTracking().SingleOrDefaultAsync(
                    value => value.ReleaseId == request.ReleaseId.Value
                        && value.RoundNumber == request.RoundNumber,
                    token);
                var row = round is null
                    ? null
                    : await _dbContext.WorkflowRequests.AsNoTracking().SingleOrDefaultAsync(
                        value => value.EvaluationRoundId == round.Id
                            && value.Kind == WorkflowRequestKind.Remediation,
                        token);
                if (row is null)
                {
                    await EnsureOperationKeyIsAvailable(
                        _dbContext.WorkflowRequests,
                        operationKey,
                        "workflow request",
                        token);
                    return null;
                }

                return await ToRemediationRequest(row, token);
            },
            async token =>
            {
                var release = await RequireReleaseInPhase(
                    request.ReleaseId,
                    ProcessPhase.Evaluating,
                    token);
                var round = await _dbContext.EvaluationRounds.AsNoTracking().SingleAsync(
                    value => value.ReleaseId == request.ReleaseId.Value
                        && value.RoundNumber == request.RoundNumber,
                    token);
                var durableRound = await ReadEvaluationRound(round, token);
                if (!SameIds(request.Problems, durableRound.Results.Where(result => result.Outcome != BranchOutcome.Passed)))
                {
                    throw Conflict(ApplicationDataConflictKind.InvalidState, "The remediation problems do not match the persisted round.");
                }

                _dbContext.WorkflowRequests.Add(ToRow(request, round.Id, operationKey));
                Transition(release, ProcessPhase.WaitingForRemediation, request.CreatedAt);
                _dbContext.TimelineEntries.Add(ToRow(timelineEntry, TimelineOperationKey(operationKey)));
                return request;
            },
            cancellationToken);
    }

    public Task<RemediationSubmission> SaveRemediationSubmissionAsync(
        ReleaseId releaseId,
        RemediationSubmission submission,
        IReadOnlyCollection<EvidenceRecord> evidenceReplacements,
        TimelineEntry timelineEntry,
        string operationKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(releaseId);
        ArgumentNullException.ThrowIfNull(submission);
        ArgumentNullException.ThrowIfNull(evidenceReplacements);
        ArgumentNullException.ThrowIfNull(timelineEntry);
        operationKey = RequireOperationKey(operationKey);
        ValidateTimeline(timelineEntry, releaseId, TimelineEntryKind.RemediationSubmitted);
        if (evidenceReplacements.Any(item => item.ReleaseId != releaseId)
            || !SameEvidenceUpdates(submission, evidenceReplacements))
        {
            throw new ArgumentException("Remediation evidence replacements must match the submission update map.", nameof(evidenceReplacements));
        }

        return ExecuteReplaySafeAsync(
            async token =>
            {
                var row = await FindOperationAsync(
                    _dbContext.RemediationSubmissions, operationKey, submission.Id, value => value.Id, token);
                return row is null ? null : ToDomain(row);
            },
            async token =>
            {
                var release = await RequireReleaseInPhase(
                    releaseId,
                    ProcessPhase.WaitingForRemediation,
                    token);
                var request = await _dbContext.WorkflowRequests.SingleAsync(
                    value => value.Id == submission.RequestId
                        && value.ReleaseId == releaseId.Value
                        && value.Kind == WorkflowRequestKind.Remediation
                        && value.IsActive,
                    token);

                foreach (var evidence in evidenceReplacements)
                {
                    var current = await _dbContext.CurrentEvidence.SingleOrDefaultAsync(
                        value => value.ReleaseId == releaseId.Value
                            && value.Kind == evidence.Kind,
                        token);
                    await ValidateReplacement(evidence, current, token);
                    _dbContext.EvidenceRecords.Add(ToRow(evidence, $"{operationKey}:evidence:{evidence.Kind}"));
                    if (current is null)
                    {
                        _dbContext.CurrentEvidence.Add(ToCurrentRow(evidence));
                    }
                    else
                    {
                        current.EvidenceId = evidence.Id;
                        current.SelectedAtUtc = evidence.RecordedAt.Value;
                        current.ConcurrencyToken = NewConcurrencyToken();
                    }
                }

                _dbContext.RemediationSubmissions.Add(ToRow(submission, operationKey));
                Close(request, submission.SubmittedAt);
                Transition(release, ProcessPhase.Evaluating, submission.SubmittedAt);
                _dbContext.TimelineEntries.Add(ToRow(timelineEntry, TimelineOperationKey(operationKey)));
                return submission;
            },
            cancellationToken);
    }

    public Task<HumanDecisionRequest> OpenHumanDecisionRequestAsync(
        DecisionSnapshot snapshot,
        HumanDecisionRequest request,
        TimelineEntry timelineEntry,
        string operationKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(timelineEntry);
        operationKey = RequireOperationKey(operationKey);
        ValidateTimeline(timelineEntry, snapshot.ReleaseId, TimelineEntryKind.ApprovalRequested);
        if (request.ReleaseId != snapshot.ReleaseId || request.SnapshotId != snapshot.Id)
        {
            throw new ArgumentException("The approval request must reference the supplied snapshot.", nameof(request));
        }

        return ExecuteReplaySafeAsync(
            async token =>
            {
                var row = await FindOperationAsync(
                    _dbContext.WorkflowRequests, operationKey, request.Id, value => value.Id, token);
                if (row is null)
                {
                    return null;
                }

                var persisted = ToHumanDecisionRequest(row);
                EnsureSameRecord(persisted.SnapshotId, snapshot.Id, operationKey);
                return persisted;
            },
            async token =>
            {
                var release = await RequireReleaseInPhase(
                    snapshot.ReleaseId,
                    ProcessPhase.Evaluating,
                    token);
                var roundRow = await _dbContext.EvaluationRounds.AsNoTracking().SingleOrDefaultAsync(
                    value => value.Id == snapshot.EvaluationRoundId, token);
                if (roundRow is null)
                {
                    throw Conflict(ApplicationDataConflictKind.InvalidState, "The decision snapshot round does not exist.");
                }

                var durableRound = await ReadEvaluationRound(roundRow, token);
                if (durableRound.ReleaseId != snapshot.ReleaseId
                    || durableRound.RoundNumber != snapshot.RoundNumber
                    || !SameIds(durableRound.Results, snapshot.Sources))
                {
                    throw Conflict(
                        ApplicationDataConflictKind.InvalidState,
                        "The decision snapshot sources do not match the persisted evaluation round.");
                }

                _dbContext.DecisionSnapshots.Add(ToRow(snapshot, $"{operationKey}:snapshot"));
                foreach (var source in snapshot.Sources)
                {
                    _dbContext.DecisionSnapshotSources.Add(ToSourceRow(snapshot.Id, source));
                }

                _dbContext.WorkflowRequests.Add(ToRow(request, operationKey));
                Transition(release, ProcessPhase.WaitingForApproval, request.CreatedAt);
                _dbContext.TimelineEntries.Add(ToRow(timelineEntry, TimelineOperationKey(operationKey)));
                return request;
            },
            cancellationToken);
    }

    public Task<PersistedHumanResponse> SaveHumanResponseAsync(
        ReleaseId releaseId,
        Guid approvalRequestId,
        HumanResponse response,
        TimelineEntry timelineEntry,
        string operationKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(releaseId);
        if (approvalRequestId == Guid.Empty)
        {
            throw new ArgumentException(
                "The approval request ID cannot be empty.",
                nameof(approvalRequestId));
        }

        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(timelineEntry);
        operationKey = RequireOperationKey(operationKey);
        ValidateTimeline(timelineEntry, releaseId, TimelineEntryKind.HumanResponseAccepted);

        return ExecuteReplaySafeAsync(
            async token =>
            {
                var row = await FindOperationAsync(
                    _dbContext.HumanResponses, operationKey, response.Id, value => value.Id, token);
                if (row is null)
                {
                    return null;
                }

                var persisted = ToDomain(row);
                if (row.ApprovalRequestId != approvalRequestId
                    || persisted.Response != response)
                {
                    throw Conflict(
                        ApplicationDataConflictKind.OperationKeyReused,
                        $"Operation key '{operationKey}' was already used for a different human response.");
                }

                return persisted;
            },
            async token =>
            {
                var release = await RequireReleaseInPhase(
                    releaseId,
                    ProcessPhase.WaitingForApproval,
                    token);
                var requestRow = await _dbContext.WorkflowRequests.SingleAsync(
                    value => value.Id == approvalRequestId
                        && value.ReleaseId == releaseId.Value
                        && value.Kind == WorkflowRequestKind.Approval
                        && value.IsActive,
                    token);

                _dbContext.HumanResponses.Add(ToRow(
                    releaseId,
                    approvalRequestId,
                    response,
                    operationKey));
                Close(requestRow, response.RespondedAt);
                var phase = response.Decision == HumanDecision.Approve
                    ? ProcessPhase.Approved
                    : ProcessPhase.Rejected;
                Transition(release, phase, response.RespondedAt);

                _dbContext.TimelineEntries.Add(ToRow(timelineEntry, TimelineOperationKey(operationKey)));
                return new PersistedHumanResponse(releaseId, approvalRequestId, response);
            },
            cancellationToken);
    }

    public Task<WorkflowCorrelationRecord> SaveWorkflowCorrelationAsync(
        WorkflowCorrelationRecord correlation,
        string operationKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(correlation);
        operationKey = RequireOperationKey(operationKey);
        return ExecuteReplaySafeAsync(
            async token =>
            {
                var row = await _dbContext.WorkflowCorrelations.AsNoTracking()
                    .SingleOrDefaultAsync(value => value.OperationKey == operationKey, token);
                if (row is null)
                {
                    return null;
                }

                return ToDomain(row);
            },
            async token =>
            {
                await RequireMutableRelease(correlation.ReleaseId, token);
                var row = await _dbContext.WorkflowCorrelations.SingleOrDefaultAsync(
                    value => value.ReleaseId == correlation.ReleaseId.Value,
                    token);
                if (row is null)
                {
                    _dbContext.WorkflowCorrelations.Add(ToRow(correlation, operationKey));
                }
                else
                {
                    row.WorkflowSessionId = correlation.WorkflowSessionId;
                    row.PendingWorkflowRequestId = correlation.PendingWorkflowRequestId;
                    row.PendingDomainRequestId = correlation.PendingDomainRequestId;
                    row.PendingRequestKind = correlation.PendingRequestKind;
                    row.CorrelatedAtUtc = correlation.CorrelatedAt.Value;
                    row.OperationKey = operationKey;
                    row.ConcurrencyToken = NewConcurrencyToken();
                }

                return correlation;
            },
            cancellationToken);
    }

    public Task<Release> MarkWorkflowFailedAsync(
        ReleaseId releaseId,
        UtcInstant failedAt,
        TimelineEntry timelineEntry,
        string operationKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(releaseId);
        ArgumentNullException.ThrowIfNull(timelineEntry);
        _dbContext.ChangeTracker.Clear();
        operationKey = RequireOperationKey(operationKey);
        ValidateTimeline(timelineEntry, releaseId, TimelineEntryKind.WorkflowFailed);
        return ExecuteReplaySafeAsync(
            async token =>
            {
                var row = await _dbContext.TimelineEntries.AsNoTracking()
                    .SingleOrDefaultAsync(value => value.OperationKey == operationKey, token);
                if (row is null)
                {
                    return null;
                }

                var release = await _dbContext.Releases.AsNoTracking().SingleAsync(
                    value => value.ReleaseId == releaseId.Value,
                    token);
                return ToDomain(release);
            },
            async token =>
            {
                var release = await RequireMutableRelease(releaseId, token);
                Transition(release, ProcessPhase.Failed, failedAt);
                _dbContext.TimelineEntries.Add(ToRow(timelineEntry, operationKey));
                return ToDomain(release);
            },
            cancellationToken);
    }

    private static void EnsureSameRecord(Guid persistedId, Guid expectedId, string operationKey)
    {
        if (persistedId != expectedId)
        {
            throw Conflict(
                ApplicationDataConflictKind.OperationKeyReused,
                $"Operation key '{operationKey}' belongs to a different durable record.");
        }
    }

    private async Task<TRow?> FindOperationAsync<TRow>(
        DbSet<TRow> rows,
        string operationKey,
        Guid expectedId,
        Func<TRow, Guid> idSelector,
        CancellationToken token)
        where TRow : class, IOperationRow
    {
        var row = await rows.AsNoTracking()
            .SingleOrDefaultAsync(value => value.OperationKey == operationKey, token);
        if (row is not null)
        {
            EnsureSameRecord(idSelector(row), expectedId, operationKey);
        }

        return row;
    }

    private async Task EnsureOperationKeyIsAvailable<TRow>(
        DbSet<TRow> rows,
        string operationKey,
        string recordType,
        CancellationToken token)
        where TRow : class, IOperationRow
    {
        if (await rows.AsNoTracking().AnyAsync(value => value.OperationKey == operationKey, token))
        {
            throw Conflict(
                ApplicationDataConflictKind.OperationKeyReused,
                $"Operation key '{operationKey}' belongs to a different {recordType}.");
        }
    }

    private static void Transition(ReleaseRow row, ProcessPhase phase, UtcInstant changedAt)
    {
        _ = ToDomain(row).TransitionTo(phase, changedAt);
        row.Phase = phase;
        row.PhaseChangedAtUtc = changedAt.Value;
        row.ConcurrencyToken = NewConcurrencyToken();
    }

    private static void Close(WorkflowRequestRow row, UtcInstant closedAt)
    {
        row.IsActive = false;
        row.ClosedAtUtc = closedAt.Value;
        row.ConcurrencyToken = NewConcurrencyToken();
    }

    private static bool SameIds(IEnumerable<BranchResult> left, IEnumerable<BranchResult> right) =>
        left.Select(item => item.Id).Order().SequenceEqual(right.Select(item => item.Id).Order());

    private static bool SameEvidenceUpdates(
        RemediationSubmission submission,
        IReadOnlyCollection<EvidenceRecord> replacements) =>
        submission.EvidenceUpdates.Count == replacements.Count
        && replacements.All(item => submission.EvidenceUpdates.TryGetValue(item.Kind, out var id) && id == item.Id);

    private static BranchResultRow ToRow(BranchResult result, Guid roundId, string operationKey) => new()
    {
        Id = result.Id,
        EvaluationRoundId = roundId,
        Check = result.Check,
        Outcome = result.Outcome,
        Disposition = result.Disposition,
        PlanningReason = result.PlanningReason,
        PlanningDetail = result.PlanningDetail,
        EvidenceId = result.EvidenceId,
        EvidenceKind = result.EvidenceKind,
        ValidUntilUtc = result.ValidUntil?.Value,
        AttemptsJson = JsonSerializer.Serialize(result.Attempts, JsonOptions),
        FindingsJson = SerializeDictionary(result.Findings),
        ReuseSourceResultId = result.ReuseSourceResultId,
        ReuseSourceRound = result.ReuseSourceRound,
        OperationKey = operationKey,
    };

    private static BranchResult ToDomain(BranchResultRow row, EvaluationRoundRow round) => new(
        row.Id,
        new ReleaseId(round.ReleaseId),
        round.RoundNumber,
        row.Check,
        row.Outcome,
        row.Disposition,
        row.PlanningReason,
        row.PlanningDetail,
        row.EvidenceId,
        row.EvidenceKind,
        ToInstant(row.ValidUntilUtc),
        Deserialize<string[]>(row.AttemptsJson),
        DeserializeDictionary(row.FindingsJson),
        row.ReuseSourceResultId,
        row.ReuseSourceRound);

    private async Task<EvaluationRound> ReadEvaluationRound(EvaluationRoundRow row, CancellationToken token)
    {
        var results = await _dbContext.BranchResults.AsNoTracking()
            .Where(value => value.EvaluationRoundId == row.Id)
            .OrderBy(value => value.Check)
            .ToListAsync(token);
        return new EvaluationRound(
            row.Id,
            new ReleaseId(row.ReleaseId),
            row.RoundNumber,
            new UtcInstant(row.StartedAtUtc),
            new UtcInstant(row.CompletedAtUtc!.Value),
            results.Select(value => ToDomain(value, row)));
    }

    private async Task<ImmutableArray<EvaluationRound>> ReadEvaluationRounds(
        ReleaseId releaseId,
        CancellationToken token)
    {
        var rows = await _dbContext.EvaluationRounds.AsNoTracking()
            .Where(value => value.ReleaseId == releaseId.Value)
            .OrderBy(value => value.RoundNumber)
            .ToListAsync(token);
        var rounds = new List<EvaluationRound>(rows.Count);
        foreach (var row in rows)
        {
            rounds.Add(await ReadEvaluationRound(row, token));
        }

        return [.. rounds];
    }

    private static WorkflowRequestRow ToRow(RemediationRequest request, Guid roundId, string operationKey) => new()
    {
        Id = request.Id,
        ReleaseId = request.ReleaseId.Value,
        Kind = WorkflowRequestKind.Remediation,
        EvaluationRoundId = roundId,
        CreatedAtUtc = request.CreatedAt.Value,
        IsActive = true,
        ConcurrencyToken = NewConcurrencyToken(),
        OperationKey = operationKey,
    };

    private async Task<RemediationRequest> ToRemediationRequest(WorkflowRequestRow row, CancellationToken token)
    {
        var roundRow = await _dbContext.EvaluationRounds.AsNoTracking().SingleAsync(
            value => value.Id == row.EvaluationRoundId, token);
        var round = await ReadEvaluationRound(roundRow, token);
        return new RemediationRequest(
            row.Id,
            new ReleaseId(row.ReleaseId),
            round.RoundNumber,
            new UtcInstant(row.CreatedAtUtc),
            round.Results.Where(value => value.Outcome != BranchOutcome.Passed));
    }

    private static RemediationSubmissionRow ToRow(RemediationSubmission submission, string operationKey) => new()
    {
        Id = submission.Id,
        RequestId = submission.RequestId,
        SubmittedAtUtc = submission.SubmittedAt.Value,
        EvidenceUpdatesJson = JsonSerializer.Serialize(submission.EvidenceUpdates, JsonOptions),
        ExplicitlySelectedChecksJson = JsonSerializer.Serialize(submission.ExplicitlySelectedChecks, JsonOptions),
        OperationKey = operationKey,
    };

    private static RemediationSubmission ToDomain(RemediationSubmissionRow row) => new(
        row.Id,
        row.RequestId,
        new UtcInstant(row.SubmittedAtUtc),
        Deserialize<Dictionary<EvidenceKind, Guid>>(row.EvidenceUpdatesJson),
        Deserialize<ReadinessCheck[]>(row.ExplicitlySelectedChecksJson));

    private static DecisionSnapshotRow ToRow(
        DecisionSnapshot snapshot,
        string operationKey) => new()
        {
            Id = snapshot.Id,
            ReleaseId = snapshot.ReleaseId.Value,
            EvaluationRoundId = snapshot.EvaluationRoundId,
            RoundNumber = snapshot.RoundNumber,
            EarliestValidityBoundUtc = snapshot.EarliestValidityBound.Value,
            DecisionBrief = snapshot.DecisionBrief,
            CreatedAtUtc = snapshot.CreatedAt.Value,
            OperationKey = operationKey,
        };

    private static DecisionSnapshotSourceRow ToSourceRow(Guid snapshotId, BranchResult source) => new()
    {
        DecisionSnapshotId = snapshotId,
        Check = source.Check,
        BranchResultId = source.Id,
        EvidenceId = source.EvidenceId!.Value,
    };

    private static WorkflowRequestRow ToRow(HumanDecisionRequest request, string operationKey) => new()
    {
        Id = request.Id,
        ReleaseId = request.ReleaseId.Value,
        Kind = WorkflowRequestKind.Approval,
        DecisionSnapshotId = request.SnapshotId,
        CreatedAtUtc = request.CreatedAt.Value,
        IsActive = true,
        ConcurrencyToken = NewConcurrencyToken(),
        OperationKey = operationKey,
    };

    private static HumanDecisionRequest ToHumanDecisionRequest(WorkflowRequestRow row) => new(
        row.Id,
        new ReleaseId(row.ReleaseId),
        row.DecisionSnapshotId!.Value,
        new UtcInstant(row.CreatedAtUtc));

    private static HumanResponseRow ToRow(
        ReleaseId releaseId,
        Guid approvalRequestId,
        HumanResponse response,
        string operationKey) => new()
        {
            Id = response.Id,
            ReleaseId = releaseId.Value,
            ApprovalRequestId = approvalRequestId,
            Decision = response.Decision,
            Responder = response.Responder,
            Comment = response.Comment,
            RespondedAtUtc = response.RespondedAt.Value,
            OperationKey = operationKey,
        };

    private static PersistedHumanResponse ToDomain(HumanResponseRow row)
    {
        var response = new HumanResponse(
            row.Id,
            row.Decision,
            row.Responder,
            row.Comment,
            new UtcInstant(row.RespondedAtUtc));
        return new PersistedHumanResponse(
            new ReleaseId(row.ReleaseId),
            row.ApprovalRequestId,
            response);
    }

    private static WorkflowCorrelationRow ToRow(WorkflowCorrelationRecord correlation, string operationKey) => new()
    {
        ReleaseId = correlation.ReleaseId.Value,
        WorkflowSessionId = correlation.WorkflowSessionId,
        PendingWorkflowRequestId = correlation.PendingWorkflowRequestId,
        PendingDomainRequestId = correlation.PendingDomainRequestId,
        PendingRequestKind = correlation.PendingRequestKind,
        CorrelatedAtUtc = correlation.CorrelatedAt.Value,
        ConcurrencyToken = NewConcurrencyToken(),
        OperationKey = operationKey,
    };

    private static WorkflowCorrelationRecord ToDomain(WorkflowCorrelationRow row) => new(
        new ReleaseId(row.ReleaseId),
        row.WorkflowSessionId,
        row.PendingWorkflowRequestId,
        row.PendingDomainRequestId,
        row.PendingRequestKind,
        new UtcInstant(row.CorrelatedAtUtc));

    private async Task<ImmutableArray<RemediationRequest>> ReadRemediationRequests(ReleaseId releaseId, CancellationToken token)
    {
        var rows = await ReadRequests(releaseId, WorkflowRequestKind.Remediation, token);
        var values = new List<RemediationRequest>(rows.Count);
        foreach (var row in rows)
        {
            values.Add(await ToRemediationRequest(row, token));
        }

        return [.. values];
    }

    private async Task<ImmutableArray<RemediationSubmission>> ReadRemediationSubmissions(ReleaseId releaseId, CancellationToken token)
    {
        var requestIds = (await ReadRequests(releaseId, WorkflowRequestKind.Remediation, token)).Select(value => value.Id).ToArray();
        return [.. (await _dbContext.RemediationSubmissions.AsNoTracking()
            .Where(value => requestIds.Contains(value.RequestId))
            .ToListAsync(token))
            .OrderBy(value => value.SubmittedAtUtc)
            .Select(ToDomain)];
    }

    private async Task<ImmutableArray<DecisionSnapshot>> ReadDecisionSnapshots(ReleaseId releaseId, CancellationToken token)
    {
        var rows = (await _dbContext.DecisionSnapshots.AsNoTracking()
            .Where(value => value.ReleaseId == releaseId.Value)
            .ToListAsync(token))
            .OrderBy(value => value.CreatedAtUtc)
            .ToList();
        var rounds = (await ReadEvaluationRounds(releaseId, token)).ToDictionary(value => value.Id);
        return [.. rows.Select(row => new DecisionSnapshot(
            row.Id,
            releaseId,
            row.EvaluationRoundId,
            row.RoundNumber,
            rounds[row.EvaluationRoundId].Results,
            new UtcInstant(row.EarliestValidityBoundUtc),
            row.DecisionBrief,
            new UtcInstant(row.CreatedAtUtc)))];
    }

    private async Task<ImmutableArray<HumanDecisionRequest>> ReadHumanDecisionRequests(ReleaseId releaseId, CancellationToken token) =>
        [.. (await ReadRequests(releaseId, WorkflowRequestKind.Approval, token)).Select(ToHumanDecisionRequest)];

    private async Task<PersistedHumanResponse?> ReadHumanResponse(
        ReleaseId releaseId,
        CancellationToken token)
    {
        var row = await _dbContext.HumanResponses.AsNoTracking().SingleOrDefaultAsync(
            value => value.ReleaseId == releaseId.Value,
            token);
        return row is null ? null : ToDomain(row);
    }

    private async Task<WorkflowCorrelationRecord?> ReadWorkflowCorrelation(ReleaseId releaseId, CancellationToken token)
    {
        var row = await _dbContext.WorkflowCorrelations.AsNoTracking().SingleOrDefaultAsync(
            value => value.ReleaseId == releaseId.Value, token);
        return row is null ? null : ToDomain(row);
    }

    private async Task<List<WorkflowRequestRow>> ReadRequests(
        ReleaseId releaseId,
        WorkflowRequestKind kind,
        CancellationToken token) =>
        (await _dbContext.WorkflowRequests.AsNoTracking()
            .Where(value => value.ReleaseId == releaseId.Value && value.Kind == kind)
            .ToListAsync(token))
            .OrderBy(value => value.CreatedAtUtc)
            .ToList();
}
