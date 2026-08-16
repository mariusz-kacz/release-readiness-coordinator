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
        ValidateTimeline(timelineEntry, round.ReleaseRevision, TimelineEntryKind.EvaluationCompleted);

        return ExecuteIdempotentAsync(
            async token =>
            {
                var row = await FindOperationAsync(
                    _dbContext.EvaluationRounds, operationKey, round.Id, value => value.Id, token);
                if (row is null)
                {
                    return null;
                }

                return await ReadEvaluationRound(row, token);
            },
            async token =>
            {
                await RequireMutableRelease(round.ReleaseRevision, token);
                _dbContext.EvaluationRounds.Add(new EvaluationRoundRow
                {
                    Id = round.Id,
                    ReleaseId = round.ReleaseRevision.ReleaseId,
                    Revision = round.ReleaseRevision.Revision,
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
            _ => InvalidConflict("The evaluation round conflicted with durable state."),
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
        ValidateTimeline(timelineEntry, request.ReleaseRevision, TimelineEntryKind.RemediationRequested);

        return ExecuteIdempotentAsync(
            async token =>
            {
                var row = await FindOperationAsync(
                    _dbContext.WorkflowRequests, operationKey, request.Id, value => value.Id, token);
                if (row is null)
                {
                    return null;
                }

                return await ToRemediationRequest(row, token);
            },
            async token =>
            {
                var release = await RequireMutableRelease(request.ReleaseRevision, token);
                var round = await _dbContext.EvaluationRounds.AsNoTracking().SingleAsync(
                    value => value.ReleaseId == request.ReleaseRevision.ReleaseId
                        && value.Revision == request.ReleaseRevision.Revision
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
            _ => InvalidConflict("The remediation request conflicted with durable state."),
            cancellationToken);
    }

    public Task<RemediationSubmission> SaveRemediationSubmissionAsync(
        ReleaseRevisionKey releaseRevision,
        RemediationSubmission submission,
        IReadOnlyCollection<EvidenceRecord> evidenceReplacements,
        TimelineEntry timelineEntry,
        string operationKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(releaseRevision);
        ArgumentNullException.ThrowIfNull(submission);
        ArgumentNullException.ThrowIfNull(evidenceReplacements);
        ArgumentNullException.ThrowIfNull(timelineEntry);
        operationKey = RequireOperationKey(operationKey);
        ValidateTimeline(timelineEntry, releaseRevision, TimelineEntryKind.RemediationSubmitted);
        if (evidenceReplacements.Any(item => item.ReleaseRevision != releaseRevision)
            || !SameEvidenceUpdates(submission, evidenceReplacements))
        {
            throw new ArgumentException("Remediation evidence replacements must match the submission update map.", nameof(evidenceReplacements));
        }

        return ExecuteIdempotentAsync(
            async token =>
            {
                var row = await FindOperationAsync(
                    _dbContext.RemediationSubmissions, operationKey, submission.Id, value => value.Id, token);
                return row is null ? null : ToDomain(row);
            },
            async token =>
            {
                var release = await RequireMutableRelease(releaseRevision, token);
                var request = await _dbContext.WorkflowRequests.SingleAsync(
                    value => value.Id == submission.RequestId
                        && value.ReleaseId == releaseRevision.ReleaseId
                        && value.Revision == releaseRevision.Revision
                        && value.Kind == WorkflowRequestKind.Remediation
                        && value.IsActive,
                    token);

                foreach (var evidence in evidenceReplacements)
                {
                    var current = await _dbContext.CurrentEvidence.SingleOrDefaultAsync(
                        value => value.ReleaseId == releaseRevision.ReleaseId
                            && value.Revision == releaseRevision.Revision
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
            _ => InvalidConflict("The remediation submission conflicted with durable state."),
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
        ValidateTimeline(timelineEntry, snapshot.ReleaseRevision, TimelineEntryKind.ApprovalRequested);
        if (request.ReleaseRevision != snapshot.ReleaseRevision || request.SnapshotId != snapshot.Id)
        {
            throw new ArgumentException("The approval request must reference the supplied snapshot.", nameof(request));
        }

        return ExecuteIdempotentAsync(
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
                var release = await RequireMutableRelease(snapshot.ReleaseRevision, token);
                var roundRow = await _dbContext.EvaluationRounds.AsNoTracking().SingleOrDefaultAsync(
                    value => value.Id == snapshot.EvaluationRoundId, token);
                if (roundRow is null)
                {
                    throw Conflict(ApplicationDataConflictKind.InvalidState, "The decision snapshot round does not exist.");
                }

                var durableRound = await ReadEvaluationRound(roundRow, token);
                if (durableRound.ReleaseRevision != snapshot.ReleaseRevision
                    || durableRound.RoundNumber != snapshot.RoundNumber
                    || !SameIds(durableRound.Results, snapshot.Sources))
                {
                    throw Conflict(
                        ApplicationDataConflictKind.InvalidState,
                        "The decision snapshot sources do not match the persisted evaluation round.");
                }

                _dbContext.DecisionSnapshots.Add(ToRow(snapshot, request.ConcurrencyToken, $"{operationKey}:snapshot"));
                foreach (var source in snapshot.Sources)
                {
                    _dbContext.DecisionSnapshotSources.Add(ToSourceRow(snapshot.Id, source));
                }

                _dbContext.WorkflowRequests.Add(ToRow(request, operationKey));
                Transition(release, ProcessPhase.WaitingForApproval, request.CreatedAt);
                _dbContext.TimelineEntries.Add(ToRow(timelineEntry, TimelineOperationKey(operationKey)));
                return request;
            },
            _ => InvalidConflict("The human decision request conflicted with durable state."),
            cancellationToken);
    }

    public Task<PersistedHumanResponse> SaveHumanResponseAsync(
        ReleaseRevisionKey releaseRevision,
        HumanResponse response,
        HumanResponseValidation validation,
        TimelineEntry timelineEntry,
        string operationKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(releaseRevision);
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(validation);
        ArgumentNullException.ThrowIfNull(timelineEntry);
        operationKey = RequireOperationKey(operationKey);
        if (validation.ResponseId != response.Id)
        {
            throw new ArgumentException("The validation must reference the supplied response.", nameof(validation));
        }

        var requiredKind = validation.State == HumanResponseValidationState.Accepted
            ? TimelineEntryKind.HumanResponseAccepted
            : TimelineEntryKind.HumanResponseDeclined;
        ValidateTimeline(timelineEntry, releaseRevision, requiredKind);

        return ExecuteIdempotentAsync(
            async token =>
            {
                var row = await FindOperationAsync(
                    _dbContext.HumanResponses, operationKey, response.Id, value => value.Id, token);
                return row is null ? null : ToDomain(row);
            },
            async token =>
            {
                var release = await RequireMutableRelease(releaseRevision, token);
                var requestRow = await _dbContext.WorkflowRequests.SingleAsync(
                    value => value.Id == response.RequestId
                        && value.ReleaseId == releaseRevision.ReleaseId
                        && value.Revision == releaseRevision.Revision
                        && value.Kind == WorkflowRequestKind.Approval,
                    token);
                if (validation.State == HumanResponseValidationState.Accepted)
                {
                    if (!requestRow.IsActive)
                    {
                        throw Conflict(
                            ApplicationDataConflictKind.InvalidState,
                            "An accepted response requires the active human decision request.");
                    }

                    ToHumanDecisionRequest(requestRow).EnsureCorrelated(response);
                }

                _dbContext.HumanResponses.Add(ToRow(releaseRevision, response, validation, operationKey));
                if (requestRow.IsActive)
                {
                    Close(requestRow, validation.ValidatedAt);
                    var phase = validation.State == HumanResponseValidationState.Declined
                        ? ProcessPhase.Evaluating
                        : response.Decision == HumanDecision.Approve ? ProcessPhase.Approved : ProcessPhase.Rejected;
                    Transition(release, phase, validation.ValidatedAt);
                }

                _dbContext.TimelineEntries.Add(ToRow(timelineEntry, TimelineOperationKey(operationKey)));
                return new PersistedHumanResponse(releaseRevision, response, validation);
            },
            _ => InvalidConflict("The human response conflicted with durable state."),
            cancellationToken);
    }

    public Task<WorkflowCorrelationRecord> SaveWorkflowCorrelationAsync(
        WorkflowCorrelationRecord correlation,
        string operationKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(correlation);
        operationKey = RequireOperationKey(operationKey);
        return ExecuteIdempotentAsync(
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
                await RequireMutableRelease(correlation.ReleaseRevision, token);
                var row = await _dbContext.WorkflowCorrelations.SingleOrDefaultAsync(
                    value => value.ReleaseId == correlation.ReleaseRevision.ReleaseId
                        && value.Revision == correlation.ReleaseRevision.Revision,
                    token);
                if (row is null)
                {
                    _dbContext.WorkflowCorrelations.Add(ToRow(correlation, operationKey));
                }
                else
                {
                    row.WorkflowSessionId = correlation.WorkflowSessionId;
                    row.PendingWorkflowRequestId = correlation.PendingWorkflowRequestId;
                    row.PendingRequestKind = correlation.PendingRequestKind;
                    row.CorrelatedAtUtc = correlation.CorrelatedAt.Value;
                    row.OperationKey = operationKey;
                    row.ConcurrencyToken = NewConcurrencyToken();
                }

                return correlation;
            },
            _ => InvalidConflict("The workflow correlation conflicted with durable state."),
            cancellationToken);
    }

    public Task<ReleaseRevision> MarkWorkflowFailedAsync(
        ReleaseRevisionKey releaseRevision,
        UtcInstant failedAt,
        TimelineEntry timelineEntry,
        string operationKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(releaseRevision);
        ArgumentNullException.ThrowIfNull(timelineEntry);
        operationKey = RequireOperationKey(operationKey);
        ValidateTimeline(timelineEntry, releaseRevision, TimelineEntryKind.WorkflowFailed);
        return ExecuteIdempotentAsync(
            async token =>
            {
                var row = await _dbContext.TimelineEntries.AsNoTracking()
                    .SingleOrDefaultAsync(value => value.OperationKey == operationKey, token);
                if (row is null)
                {
                    return null;
                }

                var release = await _dbContext.ReleaseRevisions.AsNoTracking().SingleAsync(
                    value => value.ReleaseId == releaseRevision.ReleaseId && value.Revision == releaseRevision.Revision,
                    token);
                return ToDomain(release);
            },
            async token =>
            {
                var release = await RequireMutableRelease(releaseRevision, token);
                Transition(release, ProcessPhase.Failed, failedAt);
                _dbContext.TimelineEntries.Add(ToRow(timelineEntry, operationKey));
                return ToDomain(release);
            },
            _ => InvalidConflict("The workflow failure conflicted with durable state."),
            cancellationToken);
    }

    public Task<TimelineEntry> AppendTimelineEntryAsync(
        TimelineEntry timelineEntry,
        string operationKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(timelineEntry);
        operationKey = RequireOperationKey(operationKey);
        return ExecuteIdempotentAsync(
            async token =>
            {
                var row = await _dbContext.TimelineEntries.AsNoTracking()
                    .SingleOrDefaultAsync(value => value.OperationKey == operationKey, token);
                if (row is null)
                {
                    return null;
                }

                return ToDomain(row);
            },
            async token =>
            {
                await RequireMutableRelease(timelineEntry.ReleaseRevision, token);
                _dbContext.TimelineEntries.Add(ToRow(timelineEntry, operationKey));
                return timelineEntry;
            },
            _ => InvalidConflict("The timeline append conflicted with durable state."),
            cancellationToken);
    }

    private static Task<ApplicationDataConflictException> InvalidConflict(string message) =>
        Task.FromResult(Conflict(ApplicationDataConflictKind.InvalidState, message));

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

    private static void Transition(ReleaseRevisionRow row, ProcessPhase phase, UtcInstant changedAt)
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
        PolicyVersion = result.PolicyVersion,
        ValidUntilUtc = result.ValidUntil?.Value,
        AttemptsJson = JsonSerializer.Serialize(result.Attempts, JsonOptions),
        FindingsJson = SerializeDictionary(result.Findings),
        ReuseSourceResultId = result.ReuseSourceResultId,
        ReuseSourceRound = result.ReuseSourceRound,
        OperationKey = operationKey,
    };

    private static BranchResult ToDomain(BranchResultRow row, EvaluationRoundRow round) => new(
        row.Id,
        new ReleaseRevisionKey(round.ReleaseId, round.Revision),
        round.RoundNumber,
        row.Check,
        row.Outcome,
        row.Disposition,
        row.PlanningReason,
        row.PlanningDetail,
        row.EvidenceId,
        row.EvidenceKind,
        row.PolicyVersion,
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
            new ReleaseRevisionKey(row.ReleaseId, row.Revision),
            row.RoundNumber,
            new UtcInstant(row.StartedAtUtc),
            new UtcInstant(row.CompletedAtUtc!.Value),
            results.Select(value => ToDomain(value, row)));
    }

    private async Task<ImmutableArray<EvaluationRound>> ReadEvaluationRounds(
        ReleaseRevisionKey key,
        CancellationToken token)
    {
        var rows = await _dbContext.EvaluationRounds.AsNoTracking()
            .Where(value => value.ReleaseId == key.ReleaseId && value.Revision == key.Revision)
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
        ReleaseId = request.ReleaseRevision.ReleaseId,
        Revision = request.ReleaseRevision.Revision,
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
            new ReleaseRevisionKey(row.ReleaseId, row.Revision),
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
        string concurrencyToken,
        string operationKey) => new()
        {
            Id = snapshot.Id,
            ReleaseId = snapshot.ReleaseRevision.ReleaseId,
            Revision = snapshot.ReleaseRevision.Revision,
            EvaluationRoundId = snapshot.EvaluationRoundId,
            RoundNumber = snapshot.RoundNumber,
            EarliestValidityBoundUtc = snapshot.EarliestValidityBound.Value,
            DecisionBrief = snapshot.DecisionBrief,
            CreatedAtUtc = snapshot.CreatedAt.Value,
            ConcurrencyToken = concurrencyToken,
            OperationKey = operationKey,
        };

    private static DecisionSnapshotSourceRow ToSourceRow(Guid snapshotId, BranchResult source) => new()
    {
        DecisionSnapshotId = snapshotId,
        Check = source.Check,
        BranchResultId = source.Id,
        EvidenceId = source.EvidenceId!.Value,
        PolicyVersion = source.PolicyVersion,
    };

    private static WorkflowRequestRow ToRow(HumanDecisionRequest request, string operationKey) => new()
    {
        Id = request.Id,
        ReleaseId = request.ReleaseRevision.ReleaseId,
        Revision = request.ReleaseRevision.Revision,
        Kind = WorkflowRequestKind.Approval,
        DecisionSnapshotId = request.SnapshotId,
        CreatedAtUtc = request.CreatedAt.Value,
        IsActive = true,
        ConcurrencyToken = request.ConcurrencyToken,
        OperationKey = operationKey,
    };

    private static HumanDecisionRequest ToHumanDecisionRequest(WorkflowRequestRow row) => new(
        row.Id,
        new ReleaseRevisionKey(row.ReleaseId, row.Revision),
        row.DecisionSnapshotId!.Value,
        row.ConcurrencyToken,
        new UtcInstant(row.CreatedAtUtc));

    private static HumanResponseRow ToRow(
        ReleaseRevisionKey releaseRevision,
        HumanResponse response,
        HumanResponseValidation validation,
        string operationKey) => new()
        {
            Id = response.Id,
            ReleaseId = releaseRevision.ReleaseId,
            Revision = releaseRevision.Revision,
            RequestId = response.RequestId,
            SnapshotId = response.SnapshotId,
            SnapshotConcurrencyToken = response.SnapshotConcurrencyToken,
            Decision = response.Decision,
            Responder = response.Responder,
            RespondedAtUtc = response.RespondedAt.Value,
            ValidationState = validation.State,
            ValidatedAtUtc = validation.ValidatedAt.Value,
            DeclineReasonsJson = JsonSerializer.Serialize(validation.DeclineReasons, JsonOptions),
            OperationKey = operationKey,
        };

    private static PersistedHumanResponse ToDomain(HumanResponseRow row)
    {
        var response = new HumanResponse(
            row.Id,
            row.RequestId,
            row.SnapshotId,
            row.SnapshotConcurrencyToken,
            row.Decision,
            row.Responder,
            new UtcInstant(row.RespondedAtUtc));
        var validation = row.ValidationState == HumanResponseValidationState.Accepted
            ? HumanResponseValidation.Accepted(row.Id, new UtcInstant(row.ValidatedAtUtc))
            : HumanResponseValidation.Declined(
                row.Id,
                new UtcInstant(row.ValidatedAtUtc),
                Deserialize<HumanResponseDeclineReason[]>(row.DeclineReasonsJson));
        return new PersistedHumanResponse(new ReleaseRevisionKey(row.ReleaseId, row.Revision), response, validation);
    }

    private static WorkflowCorrelationRow ToRow(WorkflowCorrelationRecord correlation, string operationKey) => new()
    {
        ReleaseId = correlation.ReleaseRevision.ReleaseId,
        Revision = correlation.ReleaseRevision.Revision,
        WorkflowSessionId = correlation.WorkflowSessionId,
        PendingWorkflowRequestId = correlation.PendingWorkflowRequestId,
        PendingRequestKind = correlation.PendingRequestKind,
        CorrelatedAtUtc = correlation.CorrelatedAt.Value,
        ConcurrencyToken = NewConcurrencyToken(),
        OperationKey = operationKey,
    };

    private static WorkflowCorrelationRecord ToDomain(WorkflowCorrelationRow row) => new(
        new ReleaseRevisionKey(row.ReleaseId, row.Revision),
        row.WorkflowSessionId,
        row.PendingWorkflowRequestId,
        row.PendingRequestKind,
        new UtcInstant(row.CorrelatedAtUtc));

    private async Task<ImmutableArray<RemediationRequest>> ReadRemediationRequests(ReleaseRevisionKey key, CancellationToken token)
    {
        var rows = await ReadRequests(key, WorkflowRequestKind.Remediation, token);
        var values = new List<RemediationRequest>(rows.Count);
        foreach (var row in rows)
        {
            values.Add(await ToRemediationRequest(row, token));
        }

        return [.. values];
    }

    private async Task<ImmutableArray<RemediationSubmission>> ReadRemediationSubmissions(ReleaseRevisionKey key, CancellationToken token)
    {
        var requestIds = (await ReadRequests(key, WorkflowRequestKind.Remediation, token)).Select(value => value.Id).ToArray();
        return [.. (await _dbContext.RemediationSubmissions.AsNoTracking()
            .Where(value => requestIds.Contains(value.RequestId))
            .ToListAsync(token))
            .OrderBy(value => value.SubmittedAtUtc)
            .Select(ToDomain)];
    }

    private async Task<ImmutableArray<DecisionSnapshot>> ReadDecisionSnapshots(ReleaseRevisionKey key, CancellationToken token)
    {
        var rows = (await _dbContext.DecisionSnapshots.AsNoTracking()
            .Where(value => value.ReleaseId == key.ReleaseId && value.Revision == key.Revision)
            .ToListAsync(token))
            .OrderBy(value => value.CreatedAtUtc)
            .ToList();
        var rounds = (await ReadEvaluationRounds(key, token)).ToDictionary(value => value.Id);
        return [.. rows.Select(row => new DecisionSnapshot(
            row.Id,
            key,
            row.EvaluationRoundId,
            row.RoundNumber,
            rounds[row.EvaluationRoundId].Results,
            new UtcInstant(row.EarliestValidityBoundUtc),
            row.DecisionBrief,
            new UtcInstant(row.CreatedAtUtc)))];
    }

    private async Task<ImmutableArray<HumanDecisionRequest>> ReadHumanDecisionRequests(ReleaseRevisionKey key, CancellationToken token) =>
        [.. (await ReadRequests(key, WorkflowRequestKind.Approval, token)).Select(ToHumanDecisionRequest)];

    private async Task<ImmutableArray<PersistedHumanResponse>> ReadHumanResponses(ReleaseRevisionKey key, CancellationToken token) =>
        [.. (await _dbContext.HumanResponses.AsNoTracking()
            .Where(value => value.ReleaseId == key.ReleaseId && value.Revision == key.Revision)
            .ToListAsync(token))
            .OrderBy(value => value.RespondedAtUtc)
            .Select(ToDomain)];

    private async Task<WorkflowCorrelationRecord?> ReadWorkflowCorrelation(ReleaseRevisionKey key, CancellationToken token)
    {
        var row = await _dbContext.WorkflowCorrelations.AsNoTracking().SingleOrDefaultAsync(
            value => value.ReleaseId == key.ReleaseId && value.Revision == key.Revision, token);
        return row is null ? null : ToDomain(row);
    }

    private async Task<List<WorkflowRequestRow>> ReadRequests(
        ReleaseRevisionKey key,
        WorkflowRequestKind kind,
        CancellationToken token) =>
        (await _dbContext.WorkflowRequests.AsNoTracking()
            .Where(value => value.ReleaseId == key.ReleaseId && value.Revision == key.Revision && value.Kind == kind)
            .ToListAsync(token))
            .OrderBy(value => value.CreatedAtUtc)
            .ToList();
}
