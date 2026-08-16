namespace ReleaseReadinessCoordinator.Simulation;

public sealed record DemoReleaseFixture(
    string Key,
    string Name,
    string ReleaseId,
    int Revision,
    string ServiceName,
    string ReleaseVersion,
    DateTimeOffset DeploymentWindowStart,
    DateTimeOffset DeploymentWindowEnd,
    IReadOnlyDictionary<string, string> DependencyRequirements,
    bool IncludeTestEvidence,
    string? TestRunVersion,
    DateTimeOffset? TestCompletedAt,
    decimal? TestPassRatePercent,
    string? CriticalSuiteFailures,
    bool IncludeSecurityEvidence,
    string? SecurityScanVersion,
    DateTimeOffset? SecurityScannedAt,
    string? CriticalFindingIds,
    string? HighFindingIds,
    string? SecurityExceptions,
    bool IncludeChangeEvidence,
    bool ChangeApproved,
    DateTimeOffset? ChangeWindowStart,
    DateTimeOffset? ChangeWindowEnd,
    bool IncludeDependencyEvidence,
    DateTimeOffset? DependencyObservedAt,
    string? DependencyStates);

public static class DemoReleaseFixtures
{
    private static readonly DateTimeOffset WindowStart =
        new(2026, 8, 20, 20, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset WindowEnd =
        new(2026, 8, 20, 21, 0, 0, TimeSpan.Zero);

    public static DemoReleaseFixture Complete { get; } = new(
        "complete",
        "Complete evidence",
        "demo-orders-2026-08",
        1,
        "orders",
        "2.4.0",
        WindowStart,
        WindowEnd,
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["inventory"] = "3.x",
        },
        true,
        "2.4.0",
        new DateTimeOffset(2026, 8, 20, 8, 0, 0, TimeSpan.Zero),
        98.5m,
        string.Empty,
        true,
        "2.4.0",
        new DateTimeOffset(2026, 8, 20, 8, 30, 0, TimeSpan.Zero),
        string.Empty,
        string.Empty,
        string.Empty,
        true,
        true,
        WindowStart.AddHours(-1),
        WindowEnd.AddHours(1),
        true,
        new DateTimeOffset(2026, 8, 20, 9, 0, 0, TimeSpan.Zero),
        "inventory|3.2.0|2026-08-20T19:00:00Z|2026-08-20T22:00:00Z||");

    public static DemoReleaseFixture MissingEvidence { get; } = new(
        "missing",
        "No initial evidence",
        "demo-orders-missing-evidence",
        1,
        "orders",
        "2.4.0",
        WindowStart,
        WindowEnd,
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["inventory"] = "3.x",
        },
        false,
        null,
        null,
        null,
        null,
        false,
        null,
        null,
        null,
        null,
        null,
        false,
        false,
        null,
        null,
        false,
        null,
        null);

    public static IReadOnlyList<DemoReleaseFixture> All { get; } = [Complete, MissingEvidence];

    public static DemoReleaseFixture? Find(string? key) =>
        All.SingleOrDefault(fixture => string.Equals(fixture.Key, key, StringComparison.Ordinal));
}
