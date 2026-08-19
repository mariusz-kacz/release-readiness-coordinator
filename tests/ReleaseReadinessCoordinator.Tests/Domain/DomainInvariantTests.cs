using ReleaseReadinessCoordinator.Domain;

namespace ReleaseReadinessCoordinator.Tests.Domain;

public sealed class DomainInvariantTests
{
    private static readonly UtcInstant SubmittedAt = Utc(2026, 8, 14, 8);
    private static readonly ReleaseId Id = new("release-42");

    [Fact]
    public void Utc_instants_reject_non_utc_offsets()
    {
        var nonUtc = new DateTimeOffset(2026, 8, 14, 10, 0, 0, TimeSpan.FromHours(2));

        Assert.Throws<ArgumentException>(() => new UtcInstant(nonUtc));
    }

    [Fact]
    public void Terminal_release_cannot_be_reopened()
    {
        var approved = Release.Create(Submission())
            .TransitionTo(ProcessPhase.Approved, Utc(2026, 8, 14, 9));

        var exception = Assert.Throws<InvalidOperationException>(
            () => approved.TransitionTo(ProcessPhase.Evaluating, Utc(2026, 8, 14, 10)));

        Assert.Contains("terminal", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Release_rejects_a_phase_change_before_its_current_phase_timestamp()
    {
        var release = Release.Create(Submission());

        Assert.Throws<ArgumentException>(
            () => release.TransitionTo(ProcessPhase.WaitingForRemediation, Utc(2026, 8, 14, 7)));
    }

    [Fact]
    public void Evidence_records_require_positive_versions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new TestEvidenceRecord(
                Guid.NewGuid(), Id, version: 0, SubmittedAt, null,
                "2.4.0", Utc(2026, 8, 14, 7), 0.97m, []));
    }

    [Fact]
    public void Evidence_records_are_typed_and_defensively_copy_collections()
    {
        var criticalFailures = new[] { "critical-login" };
        var evidence = new TestEvidenceRecord(
            Guid.NewGuid(), Id, version: 2, SubmittedAt, null,
            "2.4.0", Utc(2026, 8, 14, 7), 0.97m, criticalFailures);

        criticalFailures[0] = "mutated-after-construction";

        Assert.Equal(EvidenceKind.Test, evidence.Kind);
        Assert.True(evidence.CriticalSuiteFailures.HasValue);
        Assert.Equal("critical-login", Assert.Single(evidence.CriticalSuiteFailures.Value));
    }

    [Fact]
    public void Branch_result_rejects_evidence_from_another_readiness_source()
    {
        Assert.Throws<ArgumentException>(
            () => Result(ReadinessCheck.Test, EvidenceKind.Security));
    }

    [Fact]
    public void Reused_branch_result_requires_a_passing_source_from_an_earlier_round()
    {
        Assert.Throws<ArgumentException>(
            () => Result(
                ReadinessCheck.Test,
                EvidenceKind.Test,
                BranchOutcome.Passed,
                ExecutionDisposition.Reused));

        Assert.Throws<ArgumentException>(
            () => Result(
                ReadinessCheck.Test,
                EvidenceKind.Test,
                BranchOutcome.Blocked,
                ExecutionDisposition.Reused,
                Guid.NewGuid(),
                reuseSourceRound: 1));
    }

    [Fact]
    public void Executed_branch_result_rejects_a_reuse_source()
    {
        Assert.Throws<ArgumentException>(
            () => Result(
                ReadinessCheck.Test,
                EvidenceKind.Test,
                BranchOutcome.Passed,
                ExecutionDisposition.Executed,
                Guid.NewGuid(),
                reuseSourceRound: 1));
    }

    [Fact]
    public void Branch_result_requires_planning_detail()
    {
        Assert.Throws<ArgumentException>(
            () => Result(
                ReadinessCheck.Test,
                EvidenceKind.Test,
                planningDetail: " "));
    }

    [Fact]
    public void Evaluation_contract_rejects_duplicate_omitted_and_unknown_checks()
    {
        var results = CompleteResults();
        results[2] = Result(ReadinessCheck.Test, EvidenceKind.Test);

        Assert.Throws<InvalidOperationException>(
            () => new EvaluationRound(
                Guid.NewGuid(), Id, roundNumber: 2,
                Utc(2026, 8, 14, 8), Utc(2026, 8, 14, 9), results));

        Assert.Throws<InvalidOperationException>(
            () => new EvaluationRound(
                Guid.NewGuid(), Id, roundNumber: 2,
                Utc(2026, 8, 14, 8), Utc(2026, 8, 14, 9),
                CompleteResults().Where(result => result.Check is not ReadinessCheck.Change)));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => Result((ReadinessCheck)999, EvidenceKind.Test));
    }

    [Fact]
    public void Remediation_request_rejects_a_passing_result_as_a_problem()
    {
        var passingResult = Result(ReadinessCheck.Test, EvidenceKind.Test);

        Assert.Throws<ArgumentException>(
            () => new RemediationRequest(
                Guid.NewGuid(), Id, roundNumber: 2,
                Utc(2026, 8, 14, 9), [passingResult]));
    }

    [Fact]
    public void Decision_snapshot_rejects_duplicate_readiness_sources()
    {
        var sources = CompleteResults();
        sources[2] = sources[0];

        Assert.Throws<InvalidOperationException>(
            () => new DecisionSnapshot(
                Guid.NewGuid(), Id, Guid.NewGuid(), roundNumber: 2,
                sources,
                Utc(2026, 8, 15, 8),
                "All deterministic checks passed.",
                Utc(2026, 8, 14, 9)));
    }

    [Fact]
    public void Human_response_requires_actor_and_comment()
    {
        Assert.Throws<ArgumentException>(() => new HumanResponse(
            Guid.NewGuid(), HumanDecision.Approve, " ", "Reviewed.", Utc(2026, 8, 14, 10)));
        Assert.Throws<ArgumentException>(() => new HumanResponse(
            Guid.NewGuid(), HumanDecision.Approve, "release-manager", " ", Utc(2026, 8, 14, 10)));
    }

    [Fact]
    public void Correlation_and_timeline_vocabulary_rejects_empty_stable_identity()
    {
        Assert.Throws<ArgumentException>(
            () => new WorkflowCorrelationRecord(
                Id, workflowSessionId: " ", pendingWorkflowRequestId: "approval-request-1",
                WorkflowRequestKind.Approval, Utc(2026, 8, 14, 9)));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new TimelineEntry(
                Guid.NewGuid(), Id, sequence: 0,
                TimelineEntryKind.EvaluationCompleted, "Round completed.", Utc(2026, 8, 14, 9)));
    }

    private static ReleaseSubmission Submission() => new(
        Id,
        "checkout",
        "2.4.0",
        new UtcInterval(Utc(2026, 8, 14, 9), Utc(2026, 8, 14, 10)),
        SubmittedAt);

    private static HumanDecisionRequest Request() => new(
        Guid.NewGuid(), Id, Guid.NewGuid(), Utc(2026, 8, 14, 9));

    private static BranchResult[] CompleteResults() =>
    [
        Result(ReadinessCheck.Test, EvidenceKind.Test),
        Result(ReadinessCheck.Security, EvidenceKind.Security),
        Result(ReadinessCheck.Change, EvidenceKind.Change),
    ];

    private static BranchResult Result(
        ReadinessCheck check,
        EvidenceKind evidenceKind,
        BranchOutcome outcome = BranchOutcome.Passed,
        ExecutionDisposition disposition = ExecutionDisposition.Executed,
        Guid? reuseSourceResultId = null,
        int? reuseSourceRound = null,
        string planningDetail = "Current evidence required evaluation.") => new(
            Guid.NewGuid(),
            Id,
            roundNumber: 2,
            check,
            outcome,
            disposition,
            disposition is ExecutionDisposition.Reused
                ? PlanningReason.StillCurrent
                : PlanningReason.InitialEvaluation,
            planningDetail,
            evidenceId: Guid.NewGuid(),
            evidenceKind,
            validUntil: outcome is BranchOutcome.Passed ? Utc(2026, 8, 15, 8) : null,
            attempts: ["Evidence evaluated."],
            findings: new Dictionary<string, string> { ["ready"] = "The deterministic policy passed." },
            reuseSourceResultId,
            reuseSourceRound);

    private static UtcInstant Utc(int year, int month, int day, int hour) =>
        new(new DateTimeOffset(year, month, day, hour, 0, 0, TimeSpan.Zero));
}
