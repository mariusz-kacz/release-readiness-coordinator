using ReleaseReadinessCoordinator.Domain;

namespace ReleaseReadinessCoordinator.Tests.Domain;

public sealed class DomainInvariantTests
{
    private static readonly UtcInstant SubmittedAt = Utc(2026, 8, 14, 8);
    private static readonly ReleaseRevisionKey RevisionKey = new("release-42", 3);

    [Fact]
    public void Utc_instants_reject_non_utc_offsets()
    {
        var nonUtc = new DateTimeOffset(2026, 8, 14, 10, 0, 0, TimeSpan.FromHours(2));

        Assert.Throws<ArgumentException>(() => new UtcInstant(nonUtc));
    }

    [Fact]
    public void Terminal_release_revision_cannot_be_reopened()
    {
        var approved = ReleaseRevision.Create(Submission())
            .TransitionTo(ProcessPhase.Approved, Utc(2026, 8, 14, 9));

        var exception = Assert.Throws<InvalidOperationException>(
            () => approved.TransitionTo(ProcessPhase.Evaluating, Utc(2026, 8, 14, 10)));

        Assert.Contains("terminal", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Release_revision_rejects_a_phase_change_before_its_current_phase_timestamp()
    {
        var revision = ReleaseRevision.Create(Submission());

        Assert.Throws<ArgumentException>(
            () => revision.TransitionTo(ProcessPhase.WaitingForRemediation, Utc(2026, 8, 14, 7)));
    }

    [Fact]
    public void Evidence_records_require_positive_versions()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new TestEvidenceRecord(
                Guid.NewGuid(), RevisionKey, version: 0, SubmittedAt, null,
                "2.4.0", Utc(2026, 8, 14, 7), 0.97m, []));
    }

    [Fact]
    public void Evidence_records_are_typed_and_defensively_copy_collections()
    {
        var criticalFailures = new[] { "critical-login" };
        var evidence = new TestEvidenceRecord(
            Guid.NewGuid(), RevisionKey, version: 2, SubmittedAt, null,
            "2.4.0", Utc(2026, 8, 14, 7), 0.97m, criticalFailures);

        criticalFailures[0] = "mutated-after-construction";

        Assert.Equal(EvidenceKind.Test, evidence.Kind);
        Assert.True(evidence.CriticalSuiteFailures.HasValue);
        Assert.Equal("critical-login", Assert.Single(evidence.CriticalSuiteFailures.Value));
    }

    [Fact]
    public void Evidence_vocabulary_covers_all_three_readiness_sources()
    {
        EvidenceRecord[] evidence =
        [
            new TestEvidenceRecord(
                Guid.NewGuid(), RevisionKey, 1, SubmittedAt, null,
                "2.4.0", Utc(2026, 8, 14, 7), 0.97m, []),
            new SecurityEvidenceRecord(
                Guid.NewGuid(), RevisionKey, 1, SubmittedAt, null,
                "2.4.0", Utc(2026, 8, 14, 7), [], [],
                new Dictionary<string, (string Scope, UtcInstant ExpiresAt)>()),
            new ChangeEvidenceRecord(
                Guid.NewGuid(), RevisionKey, 1, SubmittedAt, null,
                isApproved: true,
                new UtcInterval(Utc(2026, 8, 14, 9), Utc(2026, 8, 14, 11))),
        ];

        Assert.Equal(
            [EvidenceKind.Test, EvidenceKind.Security, EvidenceKind.Change],
            evidence.Select(item => item.Kind));
    }

    [Fact]
    public void State_dimensions_are_distinct_domain_concepts()
    {
        Assert.NotEqual(typeof(ProcessPhase), typeof(BranchOutcome));
        Assert.NotEqual(typeof(BranchOutcome), typeof(ExecutionDisposition));
        Assert.NotEqual(typeof(ExecutionDisposition), typeof(PlanningReason));
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
    public void Evaluation_round_requires_exactly_one_result_per_readiness_check()
    {
        var results = CompleteResults();
        results[2] = Result(ReadinessCheck.Test, EvidenceKind.Test);

        Assert.Throws<InvalidOperationException>(
            () => new EvaluationRound(
                Guid.NewGuid(), RevisionKey, roundNumber: 2,
                Utc(2026, 8, 14, 8), Utc(2026, 8, 14, 9), results));
    }

    [Fact]
    public void Remediation_request_rejects_a_passing_result_as_a_problem()
    {
        var passingResult = Result(ReadinessCheck.Test, EvidenceKind.Test);

        Assert.Throws<ArgumentException>(
            () => new RemediationRequest(
                Guid.NewGuid(), RevisionKey, roundNumber: 2,
                Utc(2026, 8, 14, 9), [passingResult]));
    }

    [Fact]
    public void Decision_snapshot_rejects_duplicate_readiness_sources()
    {
        var sources = CompleteResults();
        sources[2] = sources[0];

        Assert.Throws<InvalidOperationException>(
            () => new DecisionSnapshot(
                Guid.NewGuid(), RevisionKey, Guid.NewGuid(), roundNumber: 2,
                sources,
                Utc(2026, 8, 15, 8),
                "All deterministic checks passed.",
                Utc(2026, 8, 14, 9)));
    }

    [Fact]
    public void Human_decision_request_accepts_only_its_correlated_snapshot_response()
    {
        var request = Request("snapshot-v1");
        var response = new HumanResponse(
            Guid.NewGuid(), request.Id, Guid.NewGuid(), "snapshot-v1",
            HumanDecision.Approve, "release-manager", Utc(2026, 8, 14, 10));

        Assert.Throws<InvalidOperationException>(() => request.EnsureCorrelated(response));
    }

    [Fact]
    public void Human_decision_request_rejects_a_stale_snapshot_concurrency_token()
    {
        var request = Request("snapshot-v2");
        var response = new HumanResponse(
            Guid.NewGuid(), request.Id, request.SnapshotId, "snapshot-v1",
            HumanDecision.Approve, "release-manager", Utc(2026, 8, 14, 10));

        Assert.Throws<InvalidOperationException>(() => request.EnsureCorrelated(response));
    }

    [Fact]
    public void Declined_response_validation_is_correlated_and_reason_coded()
    {
        var responseId = Guid.NewGuid();
        var validatedAt = Utc(2026, 8, 14, 10);

        var validation = HumanResponseValidation.Declined(
            responseId, validatedAt, [HumanResponseDeclineReason.ResultExpired]);

        Assert.Equal(responseId, validation.ResponseId);
        Assert.Equal(validatedAt, validation.ValidatedAt);
        Assert.Equal(HumanResponseDeclineReason.ResultExpired, Assert.Single(validation.DeclineReasons));
    }

    [Fact]
    public void Correlation_and_timeline_vocabulary_rejects_empty_stable_identity()
    {
        Assert.Throws<ArgumentException>(
            () => new WorkflowCorrelationRecord(
                RevisionKey, workflowSessionId: " ", pendingWorkflowRequestId: "approval-request-1",
                WorkflowRequestKind.Approval, Utc(2026, 8, 14, 9)));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => new TimelineEntry(
                Guid.NewGuid(), RevisionKey, sequence: 0,
                TimelineEntryKind.EvaluationCompleted, "Round completed.", Utc(2026, 8, 14, 9)));
    }

    private static ReleaseSubmission Submission() => new(
        RevisionKey,
        "checkout",
        "2.4.0",
        new UtcInterval(Utc(2026, 8, 14, 9), Utc(2026, 8, 14, 10)),
        SubmittedAt);

    private static HumanDecisionRequest Request(string concurrencyToken) => new(
        Guid.NewGuid(), RevisionKey, Guid.NewGuid(), concurrencyToken, Utc(2026, 8, 14, 9));

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
            RevisionKey,
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
            policyVersion: $"{check.ToString().ToLowerInvariant()}-policy/1",
            validUntil: outcome is BranchOutcome.Passed ? Utc(2026, 8, 15, 8) : null,
            attempts: ["Evidence evaluated."],
            findings: new Dictionary<string, string> { ["ready"] = "The deterministic policy passed." },
            reuseSourceResultId,
            reuseSourceRound);

    private static UtcInstant Utc(int year, int month, int day, int hour) =>
        new(new DateTimeOffset(year, month, day, hour, 0, 0, TimeSpan.Zero));
}
