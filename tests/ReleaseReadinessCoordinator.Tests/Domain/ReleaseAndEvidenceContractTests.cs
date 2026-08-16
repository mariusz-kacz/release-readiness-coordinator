using ReleaseReadinessCoordinator.Domain;

namespace ReleaseReadinessCoordinator.Tests.Domain;

public sealed class ReleaseAndEvidenceContractTests
{
    private static readonly ReleaseRevisionKey RevisionKey = new("release-42", 3);
    private static readonly UtcInstant RecordedAt = Utc(2026, 8, 14, 8);

    [Fact]
    public void Change_evidence_contains_approval_and_approved_window()
    {
        var dependencies = new Dictionary<string, string> { ["orders-api"] = "5.x" };
        var submission = new ReleaseSubmission(
            RevisionKey,
            "checkout",
            "2.4.0",
            new UtcInterval(Utc(2026, 8, 14, 9), Utc(2026, 8, 14, 10)),
            dependencies,
            RecordedAt);
        var evidenceId = Guid.NewGuid();
        var previousEvidenceId = Guid.NewGuid();
        var evidence = new ChangeEvidenceRecord(
            evidenceId,
            RevisionKey,
            version: 2,
            RecordedAt,
            previousEvidenceId,
            isApproved: true,
            new UtcInterval(Utc(2026, 8, 14, 9), Utc(2026, 8, 14, 11)));

        dependencies["orders-api"] = "mutated";

        Assert.Equal("5.x", submission.DependencyRequirements["orders-api"]);
        Assert.True(evidence.IsApproved);
        Assert.Equal(Utc(2026, 8, 14, 9), evidence.ApprovedWindow?.Start);
        Assert.Equal(Utc(2026, 8, 14, 11), evidence.ApprovedWindow?.End);
        Assert.Equal(evidenceId, evidence.Id);
        Assert.Equal(2, evidence.Version);
        Assert.Equal(previousEvidenceId, evidence.SupersedesEvidenceId);
    }

    private static UtcInstant Utc(int year, int month, int day, int hour) =>
        new(new DateTimeOffset(year, month, day, hour, 0, 0, TimeSpan.Zero));
}
