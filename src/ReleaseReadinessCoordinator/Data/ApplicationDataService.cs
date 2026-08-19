using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ReleaseReadinessCoordinator.Domain;

namespace ReleaseReadinessCoordinator.Data;

public sealed partial class ApplicationDataService(AppDbContext dbContext) : IApplicationDataService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly AppDbContext _dbContext = dbContext;
    private readonly SemaphoreSlim _currentEvidenceGate = new(1, 1);

    public async Task<EvidenceRecord?> GetCurrentEvidenceAsync(
        ReleaseId releaseId,
        EvidenceKind kind,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(releaseId);
        DomainGuard.Defined(kind, nameof(kind));

        await _currentEvidenceGate.WaitAsync(cancellationToken);
        try
        {
            var current = await _dbContext.CurrentEvidence
                .AsNoTracking()
                .SingleOrDefaultAsync(
                    row => row.ReleaseId == releaseId.Value
                        && row.Kind == kind,
                    cancellationToken);
            if (current is null)
            {
                return null;
            }

            var evidence = await _dbContext.EvidenceRecords
                .AsNoTracking()
                .SingleAsync(row => row.Id == current.EvidenceId, cancellationToken);
            return ToDomain(evidence);
        }
        finally
        {
            _currentEvidenceGate.Release();
        }
    }

    public Task<Release> SubmitReleaseAsync(
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
        ValidateInitialEvidence(submission.ReleaseId, initialEvidence);
        ValidateTimeline(timelineEntry, submission.ReleaseId, TimelineEntryKind.ReleaseSubmitted);

        return ExecuteReplaySafeAsync(
            async token =>
            {
                var existing = await _dbContext.Releases
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
                var duplicate = await _dbContext.Releases
                    .AsNoTracking()
                    .AnyAsync(
                        row => row.ReleaseId == submission.ReleaseId.Value,
                        token);
                if (duplicate)
                {
                    throw Conflict(
                        ApplicationDataConflictKind.DuplicateReleaseId,
                        $"Release '{submission.ReleaseId}' already exists.");
                }

                _dbContext.Releases.Add(ToRow(submission, operationKey));
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
                return Release.Create(submission);
            },
            cancellationToken);
    }

    public async Task<ReleaseDetailProjection?> GetReleaseDetailAsync(
        ReleaseId releaseId,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(releaseId);
        await using var transaction = await _dbContext.Database.BeginTransactionAsync(cancellationToken);
        var releaseRow = await _dbContext.Releases
            .AsNoTracking()
            .SingleOrDefaultAsync(
                row => row.ReleaseId == releaseId.Value,
                cancellationToken);
        if (releaseRow is null)
        {
            await transaction.CommitAsync(cancellationToken);
            return null;
        }

        var evidenceRows = await _dbContext.EvidenceRecords
            .AsNoTracking()
            .Where(row => row.ReleaseId == releaseId.Value)
            .OrderBy(row => row.Kind)
            .ThenBy(row => row.Version)
            .ToListAsync(cancellationToken);
        var currentRows = await _dbContext.CurrentEvidence
            .AsNoTracking()
            .Where(row => row.ReleaseId == releaseId.Value)
            .ToListAsync(cancellationToken);
        var timelineRows = await _dbContext.TimelineEntries
            .AsNoTracking()
            .Where(row => row.ReleaseId == releaseId.Value)
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
            await ReadEvaluationRounds(releaseId, cancellationToken),
            await ReadRemediationRequests(releaseId, cancellationToken),
            await ReadRemediationSubmissions(releaseId, cancellationToken),
            await ReadDecisionSnapshots(releaseId, cancellationToken),
            await ReadHumanDecisionRequests(releaseId, cancellationToken),
            await ReadHumanResponse(releaseId, cancellationToken),
            await ReadWorkflowCorrelation(releaseId, cancellationToken),
            [.. timelineRows.Select(ToDomain)]);
        await transaction.CommitAsync(cancellationToken);
        return projection;
    }

    private async Task<T> ExecuteReplaySafeAsync<T>(
        Func<CancellationToken, Task<T?>> tryReplay,
        Func<CancellationToken, Task<T>> execute,
        CancellationToken cancellationToken)
        where T : class
    {
        var existing = await tryReplay(cancellationToken);
        if (existing is not null)
        {
            return existing;
        }

        var result = await execute(cancellationToken);
        await _dbContext.SaveChangesAsync(cancellationToken);
        return result;
    }

    private async Task<ReleaseRow> RequireMutableRelease(
        ReleaseId releaseId,
        CancellationToken cancellationToken)
    {
        var row = await _dbContext.Releases.SingleOrDefaultAsync(
            value => value.ReleaseId == releaseId.Value,
            cancellationToken)
            ?? throw new InvalidOperationException(
                $"Release '{releaseId}' does not exist.");
        if (row.Phase is ProcessPhase.Approved or ProcessPhase.Rejected)
        {
            throw Conflict(
                ApplicationDataConflictKind.TerminalRelease,
                $"Release '{releaseId}' is terminal and cannot be reopened.");
        }

        return row;
    }

    private async Task<ReleaseRow> RequireReleaseInPhase(
        ReleaseId releaseId,
        ProcessPhase requiredPhase,
        CancellationToken cancellationToken)
    {
        var row = await RequireMutableRelease(releaseId, cancellationToken);
        if (row.Phase != requiredPhase)
        {
            throw Conflict(
                ApplicationDataConflictKind.InvalidState,
                $"Release '{releaseId}' must be in phase "
                    + $"'{requiredPhase}' instead of '{row.Phase}'.");
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
        ReleaseRow existing,
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
        ReleaseId releaseId,
        IReadOnlyCollection<EvidenceRecord> evidence)
    {
        if (evidence.Select(item => item.Kind).Distinct().Count() != evidence.Count)
        {
            throw new ArgumentException("Initial evidence cannot repeat a branch.", nameof(evidence));
        }

        if (evidence.Any(item =>
                item.ReleaseId != releaseId
                || item.Version != 1
                || item.SupersedesEvidenceId.HasValue))
        {
            throw new ArgumentException(
                "Initial evidence must belong to the release, use version 1, and have no supersession link.",
                nameof(evidence));
        }
    }

    private static void ValidateTimeline(
        TimelineEntry timeline,
        ReleaseId releaseId,
        TimelineEntryKind? requiredKind = null)
    {
        if (timeline.ReleaseId != releaseId
            || requiredKind.HasValue && timeline.Kind != requiredKind.Value)
        {
            throw new ArgumentException("The timeline entry does not match the durable operation.", nameof(timeline));
        }
    }

    private static ReleaseRow ToRow(ReleaseSubmission submission, string operationKey) => new()
    {
        ReleaseId = submission.ReleaseId.Value,
        ServiceName = submission.ServiceName,
        ReleaseVersion = submission.ReleaseVersion,
        RequestedWindowStartUtc = submission.RequestedDeploymentWindow.Start.Value,
        RequestedWindowEndUtc = submission.RequestedDeploymentWindow.End.Value,
        SubmittedAtUtc = submission.SubmittedAt.Value,
        Phase = ProcessPhase.Evaluating,
        PhaseChangedAtUtc = submission.SubmittedAt.Value,
        OperationKey = operationKey,
        ConcurrencyToken = NewConcurrencyToken(),
    };

    private static EvidenceRecordRow ToRow(EvidenceRecord evidence, string operationKey) => new()
    {
        Id = evidence.Id,
        ReleaseId = evidence.ReleaseId.Value,
        Kind = evidence.Kind,
        Version = evidence.Version,
        RecordedAtUtc = evidence.RecordedAt.Value,
        SupersedesEvidenceId = evidence.SupersedesEvidenceId,
        PayloadJson = SerializeEvidence(evidence),
        OperationKey = operationKey,
    };

    private static CurrentEvidenceRow ToCurrentRow(EvidenceRecord evidence) => new()
    {
        ReleaseId = evidence.ReleaseId.Value,
        Kind = evidence.Kind,
        EvidenceId = evidence.Id,
        SelectedAtUtc = evidence.RecordedAt.Value,
        ConcurrencyToken = NewConcurrencyToken(),
    };

    private static TimelineEntryRow ToRow(TimelineEntry timeline, string operationKey) => new()
    {
        Id = timeline.Id,
        ReleaseId = timeline.ReleaseId.Value,
        Sequence = timeline.Sequence,
        Kind = timeline.Kind,
        Summary = timeline.Summary,
        OccurredAtUtc = timeline.OccurredAt.Value,
        OperationKey = operationKey,
    };

    private static Release ToDomain(ReleaseRow row)
    {
        var submission = new ReleaseSubmission(
            new ReleaseId(row.ReleaseId),
            row.ServiceName,
            row.ReleaseVersion,
            new UtcInterval(new UtcInstant(row.RequestedWindowStartUtc), new UtcInstant(row.RequestedWindowEndUtc)),
            new UtcInstant(row.SubmittedAtUtc));
        return RehydrateRelease(row, submission);
    }

    private static Release RehydrateRelease(ReleaseRow row, ReleaseSubmission submission)
    {
        var release = Release.Create(submission);
        return row.Phase == ProcessPhase.Evaluating
            ? release
            : release.TransitionTo(row.Phase, new UtcInstant(row.PhaseChangedAtUtc));
    }

    private static EvidenceRecord ToDomain(EvidenceRecordRow row) => row.Kind switch
    {
        EvidenceKind.Test => ToTestEvidence(row, Deserialize<TestEvidencePayload>(row.PayloadJson)),
        EvidenceKind.Security => ToSecurityEvidence(row, Deserialize<SecurityEvidencePayload>(row.PayloadJson)),
        EvidenceKind.Change => ToChangeEvidence(row, Deserialize<ChangeEvidencePayload>(row.PayloadJson)),
        _ => throw new InvalidOperationException($"Unknown evidence kind '{row.Kind}' in durable state."),
    };

    private static TestEvidenceRecord ToTestEvidence(EvidenceRecordRow row, TestEvidencePayload payload) => new(
        row.Id,
        new ReleaseId(row.ReleaseId),
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
            new ReleaseId(row.ReleaseId),
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
            new ReleaseId(row.ReleaseId),
            row.Version,
            new UtcInstant(row.RecordedAtUtc),
            row.SupersedesEvidenceId,
            payload.IsApproved,
            ToInterval(payload.ApprovedWindow));

    private static TimelineEntry ToDomain(TimelineEntryRow row) => new(
        row.Id,
        new ReleaseId(row.ReleaseId),
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
        _ => throw new ArgumentOutOfRangeException(nameof(evidence)),
    };

    private static bool SubmissionEquals(ReleaseRow row, ReleaseSubmission submission) =>
        row.ReleaseId == submission.ReleaseId.Value
        && row.ServiceName == submission.ServiceName
        && row.ReleaseVersion == submission.ReleaseVersion
        && row.RequestedWindowStartUtc == submission.RequestedDeploymentWindow.Start.Value
        && row.RequestedWindowEndUtc == submission.RequestedDeploymentWindow.End.Value
        && row.SubmittedAtUtc == submission.SubmittedAt.Value;

    internal static bool EvidenceEquals(EvidenceRecord left, EvidenceRecord right) =>
        left.Id == right.Id
        && left.ReleaseId == right.ReleaseId
        && left.Version == right.Version
        && left.RecordedAt == right.RecordedAt
        && left.SupersedesEvidenceId == right.SupersedesEvidenceId
        && left.Kind == right.Kind
        && SerializeEvidence(left) == SerializeEvidence(right);

    private static bool TimelineEquals(TimelineEntry left, TimelineEntry right) =>
        left.ReleaseId == right.ReleaseId
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

    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, JsonOptions)
        ?? throw new InvalidOperationException($"Durable JSON for '{typeof(T).Name}' is null or invalid.");

    private static UtcInstant? ToInstant(DateTimeOffset? value) =>
        value.HasValue ? new UtcInstant(value.Value) : null;

    private static UtcInterval? ToInterval(IntervalPayload? value) =>
        value is null
            ? null
            : new UtcInterval(new UtcInstant(value.StartUtc), new UtcInstant(value.EndUtc));

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

    private static ApplicationDataConflictException Conflict(
        ApplicationDataConflictKind kind,
        string message) =>
        new(kind, message);

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

    private sealed record IntervalPayload(DateTimeOffset StartUtc, DateTimeOffset EndUtc);
}
