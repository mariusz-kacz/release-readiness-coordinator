using ReleaseReadinessCoordinator.Domain;

namespace ReleaseReadinessCoordinator.Tests.Domain;

public sealed class EvidenceIdentityTests
{
    private static readonly ReleaseRevisionKey RevisionKey = new("release-42", 3);
    private static readonly UtcInstant ObservedAt = Utc(2026, 8, 14, 8);

    [Fact]
    public void Equal_facts_in_a_new_evidence_version_change_only_the_matching_branch_identity()
    {
        var previousTestEvidence = TestEvidence(Guid.NewGuid(), version: 1, supersedesEvidenceId: null);
        var sourceEvidenceIds = new Dictionary<ReadinessCheck, Guid>
        {
            [ReadinessCheck.Test] = previousTestEvidence.Id,
            [ReadinessCheck.Security] = Guid.NewGuid(),
            [ReadinessCheck.Change] = Guid.NewGuid(),
            [ReadinessCheck.Dependency] = Guid.NewGuid(),
        };
        var currentEvidence = new Dictionary<ReadinessCheck, Guid>(sourceEvidenceIds);
        var replacement = TestEvidence(Guid.NewGuid(), version: 2, previousTestEvidence.Id);

        currentEvidence[ReadinessCheck.Test] = replacement.Id;

        var changedChecks = sourceEvidenceIds
            .Where(pair => pair.Value != currentEvidence[pair.Key])
            .Select(pair => pair.Key);

        Assert.Equal([ReadinessCheck.Test], changedChecks);
        Assert.Equal(previousTestEvidence.Id, replacement.SupersedesEvidenceId);
        Assert.Equal(previousTestEvidence.TestRunVersion, replacement.TestRunVersion);
        Assert.Equal(previousTestEvidence.CompletedAt, replacement.CompletedAt);
        Assert.Equal(previousTestEvidence.PassRate, replacement.PassRate);
        Assert.Equal(previousTestEvidence.CriticalSuiteFailures, replacement.CriticalSuiteFailures);
    }

    [Theory]
    [InlineData("ReleaseReadinessCoordinator.Domain.Fingerprints")]
    [InlineData("ReleaseReadinessCoordinator.Domain.ResultInvalidation")]
    [InlineData("ReleaseReadinessCoordinator.Domain.InvalidationMap")]
    [InlineData("ReleaseReadinessCoordinator.Domain.ResultValidity")]
    [InlineData("ReleaseReadinessCoordinator.Domain.ResultValidityState")]
    [InlineData("ReleaseReadinessCoordinator.Domain.ResultValidityReason")]
    [InlineData("ReleaseReadinessCoordinator.Domain.InputGeneration")]
    public void Superseded_change_detection_types_are_absent(string typeName)
    {
        Assert.Null(typeof(EvidenceRecord).Assembly.GetType(typeName));
    }

    private static TestEvidenceRecord TestEvidence(
        Guid id,
        int version,
        Guid? supersedesEvidenceId) => new(
            id,
            RevisionKey,
            version,
            ObservedAt,
            supersedesEvidenceId,
            "2.4.0",
            ObservedAt,
            0.97m,
            []);

    private static UtcInstant Utc(int year, int month, int day, int hour) =>
        new(new DateTimeOffset(year, month, day, hour, 0, 0, TimeSpan.Zero));
}

public sealed class FreshnessTests
{
    private static readonly UtcInstant ObservedAt = Utc(2026, 8, 14, 8);

    [Theory]
    [InlineData(ReadinessCheck.Test)]
    [InlineData(ReadinessCheck.Security)]
    [InlineData(ReadinessCheck.Dependency)]
    public void Freshness_deadline_uses_the_24_hour_maximum(ReadinessCheck check)
    {
        var deadline = FreshnessDeadlines.Calculate(Evidence(check));

        Assert.Equal(Utc(2026, 8, 15, 8), deadline);
    }

    [Theory]
    [InlineData(ReadinessCheck.Test)]
    [InlineData(ReadinessCheck.Security)]
    [InlineData(ReadinessCheck.Dependency)]
    public void Earlier_evidence_bound_wins_over_the_24_hour_maximum(ReadinessCheck check)
    {
        var earlierBound = Utc(2026, 8, 14, 14);

        var deadline = FreshnessDeadlines.Calculate(Evidence(check), earlierBound);

        Assert.Equal(earlierBound, deadline);
    }

    [Fact]
    public void Later_evidence_bound_cannot_extend_the_24_hour_maximum()
    {
        var deadline = FreshnessDeadlines.Calculate(
            Evidence(ReadinessCheck.Security),
            Utc(2026, 8, 16, 8));

        Assert.Equal(Utc(2026, 8, 15, 8), deadline);
    }

    [Theory]
    [InlineData(-1, true)]
    [InlineData(0, false)]
    public void Deadline_is_current_only_before_the_boundary(int ticksFromDeadline, bool expected)
    {
        var deadline = Utc(2026, 8, 15, 8);
        var timeProvider = new FakeTimeProvider(deadline.Value.AddTicks(ticksFromDeadline));

        Assert.Equal(expected, FreshnessDeadlines.IsCurrent(deadline, timeProvider));
    }

    [Fact]
    public void Change_evidence_does_not_use_the_24_hour_freshness_policy()
    {
        var evidence = new ChangeEvidenceRecord(
            Guid.NewGuid(), Revision(), 1, ObservedAt, null,
            isApproved: true,
            new UtcInterval(Utc(2026, 8, 14, 9), Utc(2026, 8, 14, 11)),
            "Restore the previous application package.");

        Assert.Throws<ArgumentException>(() => FreshnessDeadlines.Calculate(evidence));
    }

    private static EvidenceRecord Evidence(ReadinessCheck check) => check switch
    {
        ReadinessCheck.Test => new TestEvidenceRecord(
            Guid.NewGuid(), Revision(), 1, ObservedAt, null,
            "2.4.0", ObservedAt, 0.97m, []),
        ReadinessCheck.Security => new SecurityEvidenceRecord(
            Guid.NewGuid(), Revision(), 1, ObservedAt, null,
            "2.4.0", ObservedAt, [], [],
            new Dictionary<string, (string Scope, UtcInstant ExpiresAt)>()),
        ReadinessCheck.Dependency => new DependencyEvidenceRecord(
            Guid.NewGuid(), Revision(), 1, ObservedAt, null,
            ObservedAt,
            new Dictionary<string, DependencyState>()),
        _ => throw new ArgumentOutOfRangeException(nameof(check)),
    };

    private static ReleaseRevisionKey Revision() => new("release-42", 3);

    private static UtcInstant Utc(int year, int month, int day, int hour) =>
        new(new DateTimeOffset(year, month, day, hour, 0, 0, TimeSpan.Zero));

    private sealed class FakeTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
