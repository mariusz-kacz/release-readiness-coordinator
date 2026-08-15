using System.Collections.Immutable;

namespace ReleaseReadinessCoordinator.Domain;

public enum EvidenceKind
{
    Test = 1,
    Security = 2,
    Change = 3,
    Dependency = 4,
}

public abstract record EvidenceRecord
{
    protected EvidenceRecord(
        Guid id,
        ReleaseRevisionKey releaseRevision,
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
        ReleaseRevision = releaseRevision;
        Version = version;
        RecordedAt = recordedAt;
        SupersedesEvidenceId = supersedesEvidenceId;
    }

    public Guid Id { get; }

    public ReleaseRevisionKey ReleaseRevision { get; }

    public int Version { get; }

    public UtcInstant RecordedAt { get; }

    public Guid? SupersedesEvidenceId { get; }

    public abstract EvidenceKind Kind { get; }
}

public sealed record TestEvidenceRecord : EvidenceRecord
{
    public TestEvidenceRecord(
        Guid id,
        ReleaseRevisionKey releaseRevision,
        int version,
        UtcInstant recordedAt,
        Guid? supersedesEvidenceId,
        string? testRunVersion,
        UtcInstant? completedAt,
        decimal? passRate,
        IEnumerable<string>? criticalSuiteFailures)
        : base(id, releaseRevision, version, recordedAt, supersedesEvidenceId)
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
        ReleaseRevisionKey releaseRevision,
        int version,
        UtcInstant recordedAt,
        Guid? supersedesEvidenceId,
        string? scanVersion,
        UtcInstant? scannedAt,
        IEnumerable<string>? unresolvedCriticalFindingIds,
        IEnumerable<string>? unresolvedHighFindingIds,
        IReadOnlyDictionary<string, (string Scope, UtcInstant ExpiresAt)>? approvedExceptions)
        : base(id, releaseRevision, version, recordedAt, supersedesEvidenceId)
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
        ReleaseRevisionKey releaseRevision,
        int version,
        UtcInstant recordedAt,
        Guid? supersedesEvidenceId,
        bool? isApproved,
        UtcInterval? approvedWindow,
        string? rollbackPlan)
        : base(id, releaseRevision, version, recordedAt, supersedesEvidenceId)
    {
        IsApproved = isApproved;
        ApprovedWindow = approvedWindow;
        RollbackPlan = rollbackPlan?.Trim();
    }

    public override EvidenceKind Kind => EvidenceKind.Change;

    public bool? IsApproved { get; }

    public UtcInterval? ApprovedWindow { get; }

    public string? RollbackPlan { get; }
}

public sealed record DependencyState
{
    public DependencyState(
        string? availableVersion,
        IEnumerable<UtcInterval>? availabilityIntervals,
        IEnumerable<UtcInterval>? maintenanceIntervals)
    {
        AvailableVersion = availableVersion?.Trim();
        AvailabilityIntervals = availabilityIntervals is null
            ? null
            : DomainGuard.Copy(availabilityIntervals, nameof(availabilityIntervals));
        MaintenanceIntervals = maintenanceIntervals is null
            ? null
            : DomainGuard.Copy(maintenanceIntervals, nameof(maintenanceIntervals));
    }

    public string? AvailableVersion { get; }

    public ImmutableArray<UtcInterval>? AvailabilityIntervals { get; }

    public ImmutableArray<UtcInterval>? MaintenanceIntervals { get; }
}

public sealed record DependencyEvidenceRecord : EvidenceRecord
{
    public DependencyEvidenceRecord(
        Guid id,
        ReleaseRevisionKey releaseRevision,
        int version,
        UtcInstant recordedAt,
        Guid? supersedesEvidenceId,
        UtcInstant? observedAt,
        IReadOnlyDictionary<string, DependencyState>? dependencies)
        : base(id, releaseRevision, version, recordedAt, supersedesEvidenceId)
    {
        ObservedAt = observedAt;
        Dependencies = dependencies?.ToImmutableDictionary(
            pair => DomainGuard.Required(pair.Key, nameof(dependencies)),
            pair => pair.Value,
            StringComparer.Ordinal);
    }

    public override EvidenceKind Kind => EvidenceKind.Dependency;

    public UtcInstant? ObservedAt { get; }

    public ImmutableDictionary<string, DependencyState>? Dependencies { get; }
}
