using ReleaseReadinessCoordinator.Domain;

namespace ReleaseReadinessCoordinator.Tests.Domain;

public sealed class EvidenceIdentityTests
{
    private static readonly ReleaseId Id = new("release-42");
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

    private static TestEvidenceRecord TestEvidence(
        Guid id,
        int version,
        Guid? supersedesEvidenceId) => new(
            id,
            Id,
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
