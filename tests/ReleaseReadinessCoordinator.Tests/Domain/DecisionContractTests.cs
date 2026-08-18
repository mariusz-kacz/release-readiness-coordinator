using ReleaseReadinessCoordinator.Domain;

namespace ReleaseReadinessCoordinator.Tests.Domain;

public sealed class DecisionContractTests
{
    private static readonly ReleaseRevisionKey RevisionKey = new("release-42", 3);
    private static readonly UtcInstant ValidUntil = Utc(2026, 8, 15, 8);

    [Fact]
    public void Decision_snapshot_captures_three_passing_sources_and_the_earliest_deadline()
    {
        var sources = CompleteResults();
        sources[1] = Result(
            ReadinessCheck.Security,
            Guid.NewGuid(),
            Utc(2026, 8, 15, 7));

        var snapshot = new DecisionSnapshot(
            Guid.NewGuid(),
            RevisionKey,
            Guid.NewGuid(),
            roundNumber: 2,
            sources,
            Utc(2026, 8, 15, 7),
            "All deterministic checks passed.",
            Utc(2026, 8, 14, 9));

        Assert.Equal(3, snapshot.Sources.Length);
        Assert.All(snapshot.Sources, source =>
        {
            Assert.NotNull(source.EvidenceId);
            Assert.NotNull(source.ValidUntil);
        });
        Assert.Equal(Utc(2026, 8, 15, 7), snapshot.EarliestValidityBound);
        Assert.Equal("All deterministic checks passed.", snapshot.DecisionBrief);
        Assert.Null(typeof(DecisionSnapshot).GetProperty("ResultFingerprints"));
        Assert.Null(typeof(DecisionSnapshot).GetProperty("ReleaseFingerprint"));
        Assert.Null(typeof(DecisionSnapshot).GetProperty("DecisionBriefHash"));
        Assert.Null(typeof(BranchResult).GetProperty("Validity"));
    }

    [Fact]
    public void Human_response_contains_only_terminal_intent_and_audit_fields()
    {
        var properties = typeof(HumanResponse).GetProperties().Select(property => property.Name).Order();

        Assert.Equal(
            ["Comment", "Decision", "Id", "RespondedAt", "Responder"],
            properties);
    }

    private static BranchResult[] CompleteResults() =>
    [
        Result(ReadinessCheck.Test, Guid.NewGuid(), ValidUntil),
        Result(ReadinessCheck.Security, Guid.NewGuid(), ValidUntil),
        Result(ReadinessCheck.Change, Guid.NewGuid(), ValidUntil),
    ];

    private static BranchResult Result(
        ReadinessCheck check,
        Guid evidenceId,
        UtcInstant validUntil) => new(
            Guid.NewGuid(),
            RevisionKey,
            roundNumber: 2,
            check,
            BranchOutcome.Passed,
            ExecutionDisposition.Executed,
            PlanningReason.InitialEvaluation,
            "Concise planning detail.",
            evidenceId,
            EvidenceKindFor(check),
            validUntil,
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
