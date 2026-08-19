using ReleaseReadinessCoordinator.Domain;

namespace ReleaseReadinessCoordinator.Tests.Domain;

public sealed class EvaluationContractTests
{
    private static readonly ReleaseId Id = new("release-42");
    private static readonly UtcInstant ValidUntil = Utc(2026, 8, 15, 8);

    [Fact]
    public void Evaluation_contract_exposes_only_current_planning_inputs_and_outputs()
    {
        Assert.Equal(
            [
                PlanningReason.InitialEvaluation,
                PlanningReason.PreviousResultNotPassed,
                PlanningReason.EvidenceChanged,
                PlanningReason.Expired,
                PlanningReason.ExplicitlySelected,
                PlanningReason.StillCurrent,
            ],
            Enum.GetValues<PlanningReason>());
        Assert.Equal(
            [
                "Attempts", "Check", "Disposition", "EvidenceId", "EvidenceKind", "Findings",
                "Id", "Outcome", "PlanningDetail", "PlanningReason", "ReleaseId",
                "ReuseSourceResultId", "ReuseSourceRound", "RoundNumber", "ValidUntil",
            ],
            typeof(BranchResult).GetProperties().Select(property => property.Name).Order());
    }

    [Theory]
    [InlineData(WorkDisposition.Execute, PlanningReason.StillCurrent)]
    [InlineData(WorkDisposition.Reuse, PlanningReason.InitialEvaluation)]
    [InlineData(WorkDisposition.Reuse, PlanningReason.EvidenceChanged)]
    public void Branch_work_item_rejects_invalid_disposition_and_planning_reason_combinations(
        WorkDisposition disposition,
        PlanningReason planningReason)
    {
        Assert.Throws<ArgumentException>(() => new BranchWorkItem(
            Id,
            roundNumber: 2,
            ReadinessCheck.Test,
            disposition,
            planningReason,
            "Planning detail.",
            disposition is WorkDisposition.Reuse ? Guid.NewGuid() : null));
    }

    [Fact]
    public void Branch_work_item_reuse_requires_a_source_result()
    {
        Assert.Throws<ArgumentException>(() => new BranchWorkItem(
            Id,
            roundNumber: 2,
            ReadinessCheck.Test,
            WorkDisposition.Reuse,
            PlanningReason.StillCurrent,
            "Evidence identity and deadline remain current."));
    }

    [Fact]
    public void Reuse_source_identity_cannot_be_empty()
    {
        Assert.Throws<ArgumentException>(() => new BranchWorkItem(
            Id,
            roundNumber: 2,
            ReadinessCheck.Test,
            WorkDisposition.Reuse,
            PlanningReason.StillCurrent,
            "Evidence identity and deadline remain current.",
            Guid.Empty));

        Assert.Throws<ArgumentException>(() => Result(
            ReadinessCheck.Test,
            BranchOutcome.Passed,
            Guid.NewGuid(),
            ValidUntil,
            ExecutionDisposition.Reused,
            PlanningReason.StillCurrent,
            Guid.Empty,
            sourceRound: 1));
    }

    [Fact]
    public void Passing_branch_result_requires_evidence_and_a_deadline()
    {
        Assert.Throws<ArgumentException>(() => Result(
            ReadinessCheck.Test,
            BranchOutcome.Passed,
            evidenceId: null,
            validUntil: ValidUntil));

        Assert.Throws<ArgumentException>(() => Result(
            ReadinessCheck.Test,
            BranchOutcome.Passed,
            evidenceId: Guid.NewGuid(),
            validUntil: null));
    }

    [Fact]
    public void Non_passing_branch_result_has_no_deadline_and_missing_evidence_may_have_no_record()
    {
        Assert.Throws<ArgumentException>(() => Result(
            ReadinessCheck.Test,
            BranchOutcome.Blocked,
            evidenceId: Guid.NewGuid(),
            validUntil: ValidUntil));

        var missing = Result(
            ReadinessCheck.Test,
            BranchOutcome.MissingEvidence,
            evidenceId: null,
            validUntil: null);

        Assert.Null(missing.EvidenceId);
        Assert.Null(missing.ValidUntil);
    }

    [Fact]
    public void Reused_branch_result_retains_its_source_linkage_and_still_current_reason()
    {
        var sourceId = Guid.NewGuid();

        var result = Result(
            ReadinessCheck.Security,
            BranchOutcome.Passed,
            Guid.NewGuid(),
            ValidUntil,
            ExecutionDisposition.Reused,
            PlanningReason.StillCurrent,
            sourceId,
            sourceRound: 1);

        Assert.Equal(ExecutionDisposition.Reused, result.Disposition);
        Assert.Equal(PlanningReason.StillCurrent, result.PlanningReason);
        Assert.Equal(sourceId, result.ReuseSourceResultId);
        Assert.Equal(1, result.ReuseSourceRound);
    }

    private static BranchResult Result(
        ReadinessCheck check,
        BranchOutcome outcome,
        Guid? evidenceId,
        UtcInstant? validUntil,
        ExecutionDisposition disposition = ExecutionDisposition.Executed,
        PlanningReason planningReason = PlanningReason.InitialEvaluation,
        Guid? reuseSourceResultId = null,
        int? sourceRound = null) => new(
            Guid.NewGuid(),
            Id,
            roundNumber: 2,
            check,
            outcome,
            disposition,
            planningReason,
            "Concise planning detail.",
            evidenceId,
            EvidenceKindFor(check),
            validUntil,
            attempts: ["Evidence evaluated."],
            findings: new Dictionary<string, string> { ["ready"] = "The deterministic policy passed." },
            reuseSourceResultId,
            sourceRound);

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
