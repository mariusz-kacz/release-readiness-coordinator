using ReleaseReadinessCoordinator.Domain;

namespace ReleaseReadinessCoordinator.Tests.Domain;

public sealed class DecisionContractTests
{
    private static readonly ReleaseId Id = new("release-42");
    [Fact]
    public void Decision_snapshot_captures_three_passing_sources_and_their_evidence_identities()
    {
        var sources = CompleteResults();

        var snapshot = new DecisionSnapshot(
            Guid.NewGuid(),
            Id,
            Guid.NewGuid(),
            roundNumber: 2,
            sources,
            "All deterministic checks passed.",
            Utc(2026, 8, 14, 9));

        Assert.Equal(3, snapshot.Sources.Length);
        Assert.All(snapshot.Sources, source =>
        {
            Assert.NotNull(source.EvidenceId);
        });
        Assert.Equal("All deterministic checks passed.", snapshot.DecisionBrief);
    }

    private static BranchResult[] CompleteResults() =>
    [
        Result(ReadinessCheck.Test, Guid.NewGuid()),
        Result(ReadinessCheck.Security, Guid.NewGuid()),
        Result(ReadinessCheck.Change, Guid.NewGuid()),
    ];

    private static BranchResult Result(
        ReadinessCheck check,
        Guid evidenceId) => new(
            Guid.NewGuid(),
            Id,
            roundNumber: 2,
            check,
            BranchOutcome.Passed,
            ExecutionDisposition.Executed,
            PlanningReason.InitialEvaluation,
            "Concise planning detail.",
            evidenceId,
            EvidenceKindFor(check),
            attempts: ["Evidence evaluated."],
            findings: new Dictionary<string, string> { ["ready"] = "The deterministic policy passed." },
            reuseSourceResultId: null,
            reuseSourceRound: null);

    private static EvidenceKind EvidenceKindFor(ReadinessCheck check) => check switch
    {
        ReadinessCheck.Test => EvidenceKind.Test,
        ReadinessCheck.Security => EvidenceKind.Security,
        ReadinessCheck.Change => EvidenceKind.Change,
        _ => throw new ArgumentOutOfRangeException(nameof(check)),
    };

    private static UtcInstant Utc(int year, int month, int day, int hour) =>
        new(new DateTimeOffset(year, month, day, hour, 0, 0, TimeSpan.Zero));
}
