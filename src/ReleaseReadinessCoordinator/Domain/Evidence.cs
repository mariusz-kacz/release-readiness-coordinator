using System.Collections.Immutable;

namespace ReleaseReadinessCoordinator.Domain;

public enum EvidenceKind
{
    Test = 1,
    Security = 2,
    Change = 3,
}

public abstract record EvidenceRecord
{
    protected EvidenceRecord(
        Guid id,
        ReleaseId releaseId,
        int version,
        UtcInstant recordedAt,
        Guid? supersedesEvidenceId)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("An evidence ID cannot be empty.", nameof(id));
        }

        if (version <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(version), "An evidence version must be positive.");
        }

        if (supersedesEvidenceId == id)
        {
            throw new ArgumentException("Evidence cannot supersede itself.", nameof(supersedesEvidenceId));
        }

        Id = id;
        ReleaseId = releaseId;
        Version = version;
        RecordedAt = recordedAt;
        SupersedesEvidenceId = supersedesEvidenceId;
    }

    public Guid Id { get; }

    public ReleaseId ReleaseId { get; }

    public int Version { get; }

    public UtcInstant RecordedAt { get; }

    public Guid? SupersedesEvidenceId { get; }

    public abstract EvidenceKind Kind { get; }
}

public sealed record TestEvidenceRecord : EvidenceRecord
{
    public TestEvidenceRecord(
        Guid id,
        ReleaseId releaseId,
        int version,
        UtcInstant recordedAt,
        Guid? supersedesEvidenceId,
        string? testRunVersion,
        UtcInstant? completedAt,
        decimal? passRate,
        IEnumerable<string>? criticalSuiteFailures)
        : base(id, releaseId, version, recordedAt, supersedesEvidenceId)
    {
        if (passRate is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(passRate), "A pass rate must be between zero and one.");
        }

        TestRunVersion = testRunVersion?.Trim();
        CompletedAt = completedAt;
        PassRate = passRate;
        CriticalSuiteFailures = criticalSuiteFailures is null
            ? null
            : DomainGuard.Copy(criticalSuiteFailures.Select(value => DomainGuard.Required(value, nameof(criticalSuiteFailures))), nameof(criticalSuiteFailures));
    }

    public override EvidenceKind Kind => EvidenceKind.Test;

    public string? TestRunVersion { get; }

    public UtcInstant? CompletedAt { get; }

    public decimal? PassRate { get; }

    public ImmutableArray<string>? CriticalSuiteFailures { get; }
}

public sealed record SecurityEvidenceRecord : EvidenceRecord
{
    public SecurityEvidenceRecord(
        Guid id,
        ReleaseId releaseId,
        int version,
        UtcInstant recordedAt,
        Guid? supersedesEvidenceId,
        string? scanVersion,
        UtcInstant? scannedAt,
        IEnumerable<string>? unresolvedCriticalFindingIds,
        IEnumerable<string>? unresolvedHighFindingIds,
        IReadOnlyDictionary<string, (string Scope, UtcInstant ExpiresAt)>? approvedExceptions)
        : base(id, releaseId, version, recordedAt, supersedesEvidenceId)
    {
        ScanVersion = scanVersion?.Trim();
        ScannedAt = scannedAt;
        UnresolvedCriticalFindingIds = CopyFindingIds(unresolvedCriticalFindingIds);
        UnresolvedHighFindingIds = CopyFindingIds(unresolvedHighFindingIds);
        ApprovedExceptions = approvedExceptions?.ToImmutableDictionary(
            pair => DomainGuard.Required(pair.Key, nameof(approvedExceptions)),
            pair => (DomainGuard.Required(pair.Value.Scope, nameof(approvedExceptions)), pair.Value.ExpiresAt),
            StringComparer.Ordinal);
    }

    public override EvidenceKind Kind => EvidenceKind.Security;

    public string? ScanVersion { get; }

    public UtcInstant? ScannedAt { get; }

    public ImmutableArray<string>? UnresolvedCriticalFindingIds { get; }

    public ImmutableArray<string>? UnresolvedHighFindingIds { get; }

    public ImmutableDictionary<string, (string Scope, UtcInstant ExpiresAt)>? ApprovedExceptions { get; }

    private static ImmutableArray<string>? CopyFindingIds(IEnumerable<string>? values) =>
        values is null
            ? null
            : DomainGuard.Copy(values.Select(value => DomainGuard.Required(value, nameof(values))), nameof(values));
}

public sealed record ChangeEvidenceRecord : EvidenceRecord
{
    public ChangeEvidenceRecord(
        Guid id,
        ReleaseId releaseId,
        int version,
        UtcInstant recordedAt,
        Guid? supersedesEvidenceId,
        bool? isApproved,
        UtcInterval? approvedWindow)
        : base(id, releaseId, version, recordedAt, supersedesEvidenceId)
    {
        IsApproved = isApproved;
        ApprovedWindow = approvedWindow;
    }

    public override EvidenceKind Kind => EvidenceKind.Change;

    public bool? IsApproved { get; }

    public UtcInterval? ApprovedWindow { get; }
}
