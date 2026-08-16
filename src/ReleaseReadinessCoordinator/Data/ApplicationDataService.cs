using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ReleaseReadinessCoordinator.Domain;

namespace ReleaseReadinessCoordinator.Data;

public sealed partial class ApplicationDataService(AppDbContext dbContext) : IApplicationDataService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly AppDbContext _dbContext = dbContext;

    public Task<ReleaseRevision> SubmitReleaseAsync(
        ReleaseSubmission submission,
        IReadOnlyCollection<EvidenceRecord> initialEvidence,
        TimelineEntry timelineEntry,
        string operationKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(submission);
        ArgumentNullException.ThrowIfNull(initialEvidence);
        ArgumentNullException.ThrowIfNull(timelineEntry);
        operationKey = RequireOperationKey(operationKey);
        ValidateInitialEvidence(submission.Key, initialEvidence);
        ValidateTimeline(timelineEntry, submission.Key, TimelineEntryKind.ReleaseSubmitted);

        return ExecuteIdempotentAsync(
            async token =>
            {
                var existing = await _dbContext.ReleaseRevisions
                    .AsNoTracking()
                    .SingleOrDefaultAsync(row => row.OperationKey == operationKey, token);
                if (existing is null)
                {
                    return null;
                }

                await EnsureSubmissionReplayMatches(
                    existing, submission, initialEvidence, timelineEntry, operationKey, token);
                return RehydrateRelease(existing, submission);
            },
            async token =>
            {
                var duplicate = await _dbContext.ReleaseRevisions
                    .AsNoTracking()
                    .AnyAsync(
                        row => row.ReleaseId == submission.Key.ReleaseId
                            && row.Revision == submission.Key.Revision,
                        token);
                if (duplicate)
                {
                    throw Conflict(
                        ApplicationDataConflictKind.DuplicateReleaseRevision,
                        $"Release revision '{submission.Key.ReleaseId}/{submission.Key.Revision}' already exists.");
                }

                _dbContext.ReleaseRevisions.Add(ToRow(submission, operationKey));
                foreach (var evidence in initialEvidence)
                {
                    _dbContext.EvidenceRecords.Add(ToRow(
                        evidence,
                        EvidenceOperationKey(operationKey, evidence.Kind)));
                    _dbContext.CurrentEvidence.Add(ToCurrentRow(evidence));
                }

                _dbContext.TimelineEntries.Add(ToRow(
                    timelineEntry,
                    TimelineOperationKey(operationKey)));
                return ReleaseRevision.Create(submission);
            },
            async token =>
            {
                var duplicate = await _dbContext.ReleaseRevisions
                    .AsNoTracking()
                    .AnyAsync(
                        row => row.ReleaseId == submission.Key.ReleaseId
                            && row.Revision == submission.Key.Revision,
                        token);
                return duplicate
                    ? Conflict(
                        ApplicationDataConflictKind.DuplicateReleaseRevision,
                        $"Release revision '{submission.Key.ReleaseId}/{submission.Key.Revision}' already exists.")
                    : Conflict(
                        ApplicationDataConflictKind.InvalidState,
                        "The release submission conflicted with durable state.");
            },
            cancellationToken);
    }

    public Task<EvidenceRecord> ReplaceEvidenceAsync(
        EvidenceRecord evidence,
        TimelineEntry timelineEntry,
        string operationKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ArgumentNullException.ThrowIfNull(timelineEntry);
        operationKey = RequireOperationKey(operationKey);
        ValidateTimeline(timelineEntry, evidence.ReleaseRevision);

        return ExecuteIdempotentAsync(
            async token =>
            {
                var existing = await _dbContext.EvidenceRecords
                    .AsNoTracking()
                    .SingleOrDefaultAsync(row => row.OperationKey == operationKey, token);
                if (existing is null)
                {
                    return null;
                }

                var persisted = ToDomain(existing);
                if (!EvidenceEquals(persisted, evidence)
                    || !await TimelineReplayMatches(timelineEntry, operationKey, token))
                {
                    throw Conflict(
                        ApplicationDataConflictKind.OperationKeyReused,
                        $"Operation key '{operationKey}' was already used for different evidence.");
                }

                return persisted;
            },
            async token =>
            {
                await RequireMutableRelease(evidence.ReleaseRevision, token);
                var current = await _dbContext.CurrentEvidence.SingleOrDefaultAsync(
                    row => row.ReleaseId == evidence.ReleaseRevision.ReleaseId
                        && row.Revision == evidence.ReleaseRevision.Revision
                        && row.Kind == evidence.Kind,
                    token);
                await ValidateReplacement(evidence, current, token);

                _dbContext.EvidenceRecords.Add(ToRow(evidence, operationKey));
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

                _dbContext.TimelineEntries.Add(ToRow(
                    timelineEntry,
                    TimelineOperationKey(operationKey)));
                return evidence;
            },
            _ => Task.FromResult(Conflict(
                ApplicationDataConflictKind.InvalidState,
                "The evidence replacement conflicted with durable state.")),
            cancellationToken);
    }

    public async Task<ReleaseDetailProjection?> GetReleaseDetailAsync(
        ReleaseRevisionKey releaseRevision,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(releaseRevision);
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        var releaseRow = await _dbContext.ReleaseRevisions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                row => row.ReleaseId == releaseRevision.ReleaseId
                    && row.Revision == releaseRevision.Revision,
                cancellationToken);
        if (releaseRow is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var evidenceRows = await _dbContext.EvidenceRecords
            .AsNoTracking()
            .Where(row => row.ReleaseId == releaseRevision.ReleaseId && row.Revision == releaseRevision.Revision)
            .OrderBy(row => row.Kind)
            .ThenBy(row => row.Version)
            .ToListAsync(cancellationToken);
        var currentRows = await _dbContext.CurrentEvidence
            .AsNoTracking()
            .Where(row => row.ReleaseId == releaseRevision.ReleaseId && row.Revision == releaseRevision.Revision)
            .ToListAsync(cancellationToken);
        var timelineRows = await _dbContext.TimelineEntries
            .AsNoTracking()
            .Where(row => row.ReleaseId == releaseRevision.ReleaseId && row.Revision == releaseRevision.Revision)
            .OrderBy(row => row.Sequence)
            .ToListAsync(cancellationToken);

        var evidenceById = evidenceRows.ToDictionary(row => row.Id, ToDomain);
        var currentEvidence = currentRows.ToImmutableDictionary(
            row => row.Kind,
            row => evidenceById.TryGetValue(row.EvidenceId, out var evidence)
                ? evidence
                : throw new InvalidOperationException(
                    $"Current {row.Kind} evidence '{row.EvidenceId}' is missing from durable history."));

        var projection = new ReleaseDetailProjection(
            ToDomain(releaseRow),
            [.. evidenceRows.Select(row => evidenceById[row.Id])],
            currentEvidence,
            await ReadEvaluationRounds(releaseRevision, cancellationToken),
            await ReadRemediationRequests(releaseRevision, cancellationToken),
            await ReadRemediationSubmissions(releaseRevision, cancellationToken),
            await ReadDecisionSnapshots(releaseRevision, cancellationToken),
            await ReadHumanDecisionRequests(releaseRevision, cancellationToken),
            await ReadHumanResponses(releaseRevision, cancellationToken),
            await ReadWorkflowCorrelation(releaseRevision, cancellationToken),
            [.. timelineRows.Select(ToDomain)]);
        await transaction.CommitAsync(cancellationToken);
        return projection;
    }

    private async Task<T> ExecuteIdempotentAsync<T>(
        Func<CancellationToken, Task<T?>> findExisting,
        Func<CancellationToken, Task<T>> write,
        Func<CancellationToken, Task<ApplicationDataConflictException>> classifyConflict,
        CancellationToken cancellationToken)
        where T : class
    {
        var existing = await findExisting(cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        try
        {
            await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
            var result = await write(cancellationToken);
            await _dbContext.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return result;
        }
        catch (DbUpdateConcurrencyException exception)
        {
            _dbContext.ChangeTracker.Clear();
            var replayed = await findExisting(cancellationToken);
            if (replayed is not null)
            {
                return replayed;
            }

            throw Conflict(
                ApplicationDataConflictKind.ConcurrentModification,
                "Durable state changed concurrently.",
                exception);
        }
        catch (DbUpdateException exception) when (IsConstraintViolation(exception))
        {
            _dbContext.ChangeTracker.Clear();
            var replayed = await findExisting(cancellationToken);
            if (replayed is not null)
            {
                return replayed;
            }

            throw await classifyConflict(cancellationToken);
        }
        catch
        {
            _dbContext.ChangeTracker.Clear();
            throw;
        }
    }

    private async Task<ReleaseRevisionRow> RequireMutableRelease(
        ReleaseRevisionKey key,
        CancellationToken cancellationToken)
    {
        var row = await _dbContext.ReleaseRevisions.SingleOrDefaultAsync(
            value => value.ReleaseId == key.ReleaseId && value.Revision == key.Revision,
            cancellationToken)
            ?? throw new InvalidOperationException(
                $"Release revision '{key.ReleaseId}/{key.Revision}' does not exist.");
        if (row.Phase is ProcessPhase.Approved or ProcessPhase.Rejected)
        {
            throw Conflict(
                ApplicationDataConflictKind.TerminalRelease,
                $"Release revision '{key.ReleaseId}/{key.Revision}' is terminal and cannot be reopened.");
        }

        return row;
    }

    private async Task ValidateReplacement(
        EvidenceRecord evidence,
        CurrentEvidenceRow? current,
        CancellationToken cancellationToken)
    {
        if (current is null)
        {
            if (evidence.Version != 1 || evidence.SupersedesEvidenceId.HasValue)
            {
                throw Conflict(
                    ApplicationDataConflictKind.InvalidState,
                    $"Initial {evidence.Kind} evidence must be version 1 and cannot supersede another record.");
            }

            return;
        }

        var currentEvidence = await _dbContext.EvidenceRecords
            .AsNoTracking()
            .SingleAsync(row => row.Id == current.EvidenceId, cancellationToken);
        if (evidence.Version != currentEvidence.Version + 1
            || evidence.SupersedesEvidenceId != currentEvidence.Id)
        {
            throw Conflict(
                ApplicationDataConflictKind.InvalidState,
                $"New {evidence.Kind} evidence must increment version {currentEvidence.Version} and supersede '{currentEvidence.Id}'.");
        }
    }

    private async Task EnsureSubmissionReplayMatches(
        ReleaseRevisionRow existing,
        ReleaseSubmission submission,
        IReadOnlyCollection<EvidenceRecord> initialEvidence,
        TimelineEntry timelineEntry,
        string operationKey,
        CancellationToken cancellationToken)
    {
        var evidencePrefix = $"{operationKey}:evidence:";
        var existingEvidence = await _dbContext.EvidenceRecords
            .AsNoTracking()
            .Where(row => row.OperationKey.StartsWith(evidencePrefix))
            .ToListAsync(cancellationToken);
        var evidenceMatches = existingEvidence.Count == initialEvidence.Count
            && initialEvidence.All(expected => existingEvidence.Any(row =>
                row.OperationKey == EvidenceOperationKey(operationKey, expected.Kind)
                && EvidenceEquals(ToDomain(row), expected)));

        if (!SubmissionEquals(existing, submission)
            || !evidenceMatches
            || !await TimelineReplayMatches(timelineEntry, operationKey, cancellationToken))
        {
            throw Conflict(
                ApplicationDataConflictKind.OperationKeyReused,
                $"Operation key '{operationKey}' was already used for a different release submission.");
        }
    }

    private async Task<bool> TimelineReplayMatches(
        TimelineEntry timelineEntry,
        string operationKey,
        CancellationToken cancellationToken)
    {
        var row = await _dbContext.TimelineEntries
            .AsNoTracking()
            .SingleOrDefaultAsync(
                value => value.OperationKey == TimelineOperationKey(operationKey),
                cancellationToken);
        return row is not null && TimelineEquals(ToDomain(row), timelineEntry);
    }

    private static void ValidateInitialEvidence(
        ReleaseRevisionKey releaseRevision,
        IReadOnlyCollection<EvidenceRecord> evidence)
    {
        if (evidence.Select(item => item.Kind).Distinct().Count() != evidence.Count)
        {
            throw new ArgumentException("Initial evidence cannot repeat a branch.", nameof(evidence));
        }

        if (evidence.Any(item =>
                item.ReleaseRevision != releaseRevision
                || item.Version != 1
                || item.SupersedesEvidenceId.HasValue))
        {
            throw new ArgumentException(
                "Initial evidence must belong to the release revision, use version 1, and have no supersession link.",
                nameof(evidence));
        }
    }

    private static void ValidateTimeline(
        TimelineEntry timeline,
        ReleaseRevisionKey releaseRevision,
        TimelineEntryKind? requiredKind = null)
    {
        if (timeline.ReleaseRevision != releaseRevision
            || requiredKind.HasValue && timeline.Kind != requiredKind.Value)
        {
            throw new ArgumentException("The timeline entry does not match the durable operation.", nameof(timeline));
        }
    }

    private static ReleaseRevisionRow ToRow(ReleaseSubmission submission, string operationKey) => new()
    {
        ReleaseId = submission.Key.ReleaseId,
        Revision = submission.Key.Revision,
        ServiceName = submission.ServiceName,
        ReleaseVersion = submission.ReleaseVersion,
        RequestedWindowStartUtc = submission.RequestedDeploymentWindow.Start.Value,
        RequestedWindowEndUtc = submission.RequestedDeploymentWindow.End.Value,
        DependencyRequirementsJson = SerializeDictionary(submission.DependencyRequirements),
        SubmittedAtUtc = submission.SubmittedAt.Value,
        Phase = ProcessPhase.Evaluating,
        PhaseChangedAtUtc = submission.SubmittedAt.Value,
        OperationKey = operationKey,
        ConcurrencyToken = NewConcurrencyToken(),
    };

    private static EvidenceRecordRow ToRow(EvidenceRecord evidence, string operationKey) => new()
    {
        Id = evidence.Id,
        ReleaseId = evidence.ReleaseRevision.ReleaseId,
        Revision = evidence.ReleaseRevision.Revision,
        Kind = evidence.Kind,
        Version = evidence.Version,
        RecordedAtUtc = evidence.RecordedAt.Value,
        SupersedesEvidenceId = evidence.SupersedesEvidenceId,
        PayloadJson = SerializeEvidence(evidence),
        OperationKey = operationKey,
    };

    private static CurrentEvidenceRow ToCurrentRow(EvidenceRecord evidence) => new()
    {
        ReleaseId = evidence.ReleaseRevision.ReleaseId,
        Revision = evidence.ReleaseRevision.Revision,
        Kind = evidence.Kind,
        EvidenceId = evidence.Id,
        SelectedAtUtc = evidence.RecordedAt.Value,
        ConcurrencyToken = NewConcurrencyToken(),
    };

    private static TimelineEntryRow ToRow(TimelineEntry timeline, string operationKey) => new()
    {
        Id = timeline.Id,
        ReleaseId = timeline.ReleaseRevision.ReleaseId,
        Revision = timeline.ReleaseRevision.Revision,
        Sequence = timeline.Sequence,
        Kind = timeline.Kind,
        Summary = timeline.Summary,
        OccurredAtUtc = timeline.OccurredAt.Value,
        OperationKey = operationKey,
    };

    private static ReleaseRevision ToDomain(ReleaseRevisionRow row)
    {
        var submission = new ReleaseSubmission(
            new ReleaseRevisionKey(row.ReleaseId, row.Revision),
            row.ServiceName,
            row.ReleaseVersion,
            new UtcInterval(new UtcInstant(row.RequestedWindowStartUtc), new UtcInstant(row.RequestedWindowEndUtc)),
            DeserializeDictionary(row.DependencyRequirementsJson),
            new UtcInstant(row.SubmittedAtUtc));
        return RehydrateRelease(row, submission);
    }

    private static ReleaseRevision RehydrateRelease(ReleaseRevisionRow row, ReleaseSubmission submission)
    {
        var release = ReleaseRevision.Create(submission);
        return row.Phase == ProcessPhase.Evaluating
            ? release
            : release.TransitionTo(row.Phase, new UtcInstant(row.PhaseChangedAtUtc));
    }

    private static EvidenceRecord ToDomain(EvidenceRecordRow row) => row.Kind switch
    {
        EvidenceKind.Test => ToTestEvidence(row, Deserialize<TestEvidencePayload>(row.PayloadJson)),
        EvidenceKind.Security => ToSecurityEvidence(row, Deserialize<SecurityEvidencePayload>(row.PayloadJson)),
        EvidenceKind.Change => ToChangeEvidence(row, Deserialize<ChangeEvidencePayload>(row.PayloadJson)),
        EvidenceKind.Dependency => ToDependencyEvidence(row, Deserialize<DependencyEvidencePayload>(row.PayloadJson)),
        _ => throw new InvalidOperationException($"Unknown evidence kind '{row.Kind}' in durable state."),
    };

    private static TestEvidenceRecord ToTestEvidence(EvidenceRecordRow row, TestEvidencePayload payload) => new(
        row.Id,
        new ReleaseRevisionKey(row.ReleaseId, row.Revision),
        row.Version,
        new UtcInstant(row.RecordedAtUtc),
        row.SupersedesEvidenceId,
        payload.TestRunVersion,
        ToInstant(payload.CompletedAtUtc),
        payload.PassRate,
        payload.CriticalSuiteFailures);

    private static SecurityEvidenceRecord ToSecurityEvidence(
        EvidenceRecordRow row,
        SecurityEvidencePayload payload) => new(
            row.Id,
            new ReleaseRevisionKey(row.ReleaseId, row.Revision),
            row.Version,
            new UtcInstant(row.RecordedAtUtc),
            row.SupersedesEvidenceId,
            payload.ScanVersion,
            ToInstant(payload.ScannedAtUtc),
            payload.UnresolvedCriticalFindingIds,
            payload.UnresolvedHighFindingIds,
            payload.ApprovedExceptions?.ToDictionary(
                item => item.FindingId,
                item => (item.Scope, new UtcInstant(item.ExpiresAtUtc)),
                StringComparer.Ordinal));

    private static ChangeEvidenceRecord ToChangeEvidence(
        EvidenceRecordRow row,
        ChangeEvidencePayload payload) => new(
            row.Id,
            new ReleaseRevisionKey(row.ReleaseId, row.Revision),
            row.Version,
            new UtcInstant(row.RecordedAtUtc),
            row.SupersedesEvidenceId,
            payload.IsApproved,
            ToInterval(payload.ApprovedWindow));

    private static DependencyEvidenceRecord ToDependencyEvidence(
        EvidenceRecordRow row,
        DependencyEvidencePayload payload) => new(
            row.Id,
            new ReleaseRevisionKey(row.ReleaseId, row.Revision),
            row.Version,
            new UtcInstant(row.RecordedAtUtc),
            row.SupersedesEvidenceId,
            ToInstant(payload.ObservedAtUtc),
            payload.Dependencies?.ToDictionary(
                item => item.Name,
                item => new DependencyState(
                    item.AvailableVersion,
                    ToIntervals(item.AvailabilityIntervals),
                    ToIntervals(item.MaintenanceIntervals)),
                StringComparer.Ordinal));

    private static TimelineEntry ToDomain(TimelineEntryRow row) => new(
        row.Id,
        new ReleaseRevisionKey(row.ReleaseId, row.Revision),
        row.Sequence,
        row.Kind,
        row.Summary,
        new UtcInstant(row.OccurredAtUtc));

    private static string SerializeEvidence(EvidenceRecord evidence) => evidence switch
    {
        TestEvidenceRecord item => JsonSerializer.Serialize(
            new TestEvidencePayload(
                item.TestRunVersion,
                item.CompletedAt?.Value,
                item.PassRate,
                item.CriticalSuiteFailures?.ToArray()),
            JsonOptions),
        SecurityEvidenceRecord item => JsonSerializer.Serialize(
            new SecurityEvidencePayload(
                item.ScanVersion,
                item.ScannedAt?.Value,
                item.UnresolvedCriticalFindingIds?.ToArray(),
                item.UnresolvedHighFindingIds?.ToArray(),
                item.ApprovedExceptions?
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => new SecurityExceptionPayload(
                        pair.Key,
                        pair.Value.Scope,
                        pair.Value.ExpiresAt.Value))
                    .ToArray()),
            JsonOptions),
        ChangeEvidenceRecord item => JsonSerializer.Serialize(
            new ChangeEvidencePayload(
                item.IsApproved,
                item.ApprovedWindow is null
                    ? null
                    : new IntervalPayload(item.ApprovedWindow.Start.Value, item.ApprovedWindow.End.Value)),
            JsonOptions),
        DependencyEvidenceRecord item => JsonSerializer.Serialize(
            new DependencyEvidencePayload(
                item.ObservedAt?.Value,
                item.Dependencies?
                    .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                    .Select(pair => new DependencyPayload(
                        pair.Key,
                        pair.Value.AvailableVersion,
                        ToPayloads(pair.Value.AvailabilityIntervals),
                        ToPayloads(pair.Value.MaintenanceIntervals)))
                    .ToArray()),
            JsonOptions),
        _ => throw new ArgumentOutOfRangeException(nameof(evidence)),
    };

    private static bool SubmissionEquals(ReleaseRevisionRow row, ReleaseSubmission submission) =>
        row.ReleaseId == submission.Key.ReleaseId
        && row.Revision == submission.Key.Revision
        && row.ServiceName == submission.ServiceName
        && row.ReleaseVersion == submission.ReleaseVersion
        && row.RequestedWindowStartUtc == submission.RequestedDeploymentWindow.Start.Value
        && row.RequestedWindowEndUtc == submission.RequestedDeploymentWindow.End.Value
        && row.SubmittedAtUtc == submission.SubmittedAt.Value
        && DictionaryEquals(DeserializeDictionary(row.DependencyRequirementsJson), submission.DependencyRequirements);

    private static bool EvidenceEquals(EvidenceRecord left, EvidenceRecord right) =>
        left.Id == right.Id
        && left.ReleaseRevision == right.ReleaseRevision
        && left.Version == right.Version
        && left.RecordedAt == right.RecordedAt
        && left.SupersedesEvidenceId == right.SupersedesEvidenceId
        && left.Kind == right.Kind
        && SerializeEvidence(left) == SerializeEvidence(right);

    private static bool TimelineEquals(TimelineEntry left, TimelineEntry right) =>
        left.ReleaseRevision == right.ReleaseRevision
        && left.Sequence == right.Sequence
        && left.Kind == right.Kind
        && left.Summary == right.Summary
        && left.OccurredAt == right.OccurredAt;

    private static string SerializeDictionary(IReadOnlyDictionary<string, string> values) =>
        JsonSerializer.Serialize(
            values.OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal),
            JsonOptions);

    private static Dictionary<string, string> DeserializeDictionary(string json) =>
        Deserialize<Dictionary<string, string>>(json);

    private static bool DictionaryEquals(
        IReadOnlyDictionary<string, string> left,
        IReadOnlyDictionary<string, string> right) =>
        left.Count == right.Count
        && left.All(pair => right.TryGetValue(pair.Key, out var value) && value == pair.Value);

    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, JsonOptions)
        ?? throw new InvalidOperationException($"Durable JSON for '{typeof(T).Name}' is null or invalid.");

    private static UtcInstant? ToInstant(DateTimeOffset? value) =>
        value.HasValue ? new UtcInstant(value.Value) : null;

    private static UtcInterval? ToInterval(IntervalPayload? value) =>
        value is null
            ? null
            : new UtcInterval(new UtcInstant(value.StartUtc), new UtcInstant(value.EndUtc));

    private static IEnumerable<UtcInterval>? ToIntervals(IntervalPayload[]? values) =>
        values?.Select(value => new UtcInterval(
            new UtcInstant(value.StartUtc),
            new UtcInstant(value.EndUtc)));

    private static IntervalPayload[]? ToPayloads(ImmutableArray<UtcInterval>? values) =>
        values?.Select(value => new IntervalPayload(value.Start.Value, value.End.Value)).ToArray();

    private static string RequireOperationKey(string operationKey)
    {
        if (string.IsNullOrWhiteSpace(operationKey))
        {
            throw new ArgumentException("A stable operation key is required.", nameof(operationKey));
        }

        operationKey = operationKey.Trim();
        if (operationKey.Length > 240)
        {
            throw new ArgumentException("An operation key cannot exceed 240 characters.", nameof(operationKey));
        }

        return operationKey;
    }

    private static bool IsConstraintViolation(DbUpdateException exception) =>
        exception.InnerException is SqliteException { SqliteErrorCode: 19 };

    private static ApplicationDataConflictException Conflict(
        ApplicationDataConflictKind kind,
        string message,
        Exception? innerException = null) =>
        innerException is null
            ? new ApplicationDataConflictException(kind, message)
            : new ApplicationDataConflictException(kind, message, innerException);

    private static string NewConcurrencyToken() => Guid.NewGuid().ToString("N");

    private static string TimelineOperationKey(string operationKey) => $"{operationKey}:timeline";

    private static string EvidenceOperationKey(string operationKey, EvidenceKind kind) =>
        $"{operationKey}:evidence:{kind}";

    private sealed record TestEvidencePayload(
        string? TestRunVersion,
        DateTimeOffset? CompletedAtUtc,
        decimal? PassRate,
        string[]? CriticalSuiteFailures);

    private sealed record SecurityEvidencePayload(
        string? ScanVersion,
        DateTimeOffset? ScannedAtUtc,
        string[]? UnresolvedCriticalFindingIds,
        string[]? UnresolvedHighFindingIds,
        SecurityExceptionPayload[]? ApprovedExceptions);

    private sealed record SecurityExceptionPayload(
        string FindingId,
        string Scope,
        DateTimeOffset ExpiresAtUtc);

    private sealed record ChangeEvidencePayload(
        bool? IsApproved,
        IntervalPayload? ApprovedWindow);

    private sealed record DependencyEvidencePayload(
        DateTimeOffset? ObservedAtUtc,
        DependencyPayload[]? Dependencies);

    private sealed record DependencyPayload(
        string Name,
        string? AvailableVersion,
        IntervalPayload[]? AvailabilityIntervals,
        IntervalPayload[]? MaintenanceIntervals);

    private sealed record IntervalPayload(DateTimeOffset StartUtc, DateTimeOffset EndUtc);
}
