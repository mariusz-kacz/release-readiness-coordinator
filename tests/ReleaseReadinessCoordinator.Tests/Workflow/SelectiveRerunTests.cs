using ReleaseReadinessCoordinator.Domain;
using ReleaseReadinessCoordinator.Readiness;
using ReleaseReadinessCoordinator.Workflow;
using BranchResult = ReleaseReadinessCoordinator.Domain.BranchResult;
using BranchWorkItem = ReleaseReadinessCoordinator.Domain.BranchWorkItem;

namespace ReleaseReadinessCoordinator.Tests.Workflow;

public sealed class SelectiveRerunPlanningTests
{
    private static readonly ReleaseId Id = new("release-42");
    private static readonly UtcInstant Now = Utc(2026, 8, 17, 10);

    [Fact]
    public void Initial_round_always_plans_one_execution_for_each_readiness_check()
    {
        var request = Request(previousResults: []);

        var work = Planner().Plan(request);

        Assert.Equal(Enum.GetValues<ReadinessCheck>(), work.Select(item => item.Check));
        Assert.All(work, item =>
        {
            Assert.Equal(WorkDisposition.Execute, item.Disposition);
            Assert.Equal(PlanningReason.InitialEvaluation, item.PlanningReason);
            Assert.Null(item.ReuseSourceResultId);
        });
    }

    [Theory]
    [MemberData(nameof(PrioritizedPlanningCases))]
    public void Planner_chooses_the_single_prioritized_reason_when_conditions_overlap(
        bool explicitlySelected,
        bool evidenceChanged,
        BranchOutcome previousOutcome,
        bool expired,
        PlanningReason expectedReason)
    {
        var source = Result(
            ReadinessCheck.Test,
            previousOutcome,
            evidenceId: TestEvidenceId,
            validUntil: previousOutcome is BranchOutcome.Passed
                ? expired ? Now : Utc(2026, 8, 17, 11)
                : null);
        var previous = PassingResults().ToDictionary(result => result.Check);
        previous[ReadinessCheck.Test] = source;
        var currentEvidence = CurrentEvidence();
        if (evidenceChanged)
        {
            currentEvidence[ReadinessCheck.Test] = Guid.NewGuid();
        }

        var request = Request(
            previous.Values,
            currentEvidence,
            explicitlySelected ? [ReadinessCheck.Test] : []);

        var item = Assert.Single(Planner().Plan(request), item => item.Check is ReadinessCheck.Test);

        Assert.Equal(
            expectedReason is PlanningReason.StillCurrent ? WorkDisposition.Reuse : WorkDisposition.Execute,
            item.Disposition);
        Assert.Equal(expectedReason, item.PlanningReason);
        Assert.Equal(
            expectedReason is PlanningReason.StillCurrent ? source.Id : null,
            item.ReuseSourceResultId);
    }

    [Fact]
    public void Replacing_one_evidence_identity_executes_only_its_matching_branch()
    {
        var currentEvidence = CurrentEvidence();
        currentEvidence[ReadinessCheck.Security] = Guid.NewGuid();

        var work = Planner().Plan(Request(PassingResults(), currentEvidence));

        Assert.Collection(
            work,
            test => Assert.Equal(WorkDisposition.Reuse, test.Disposition),
            security =>
            {
                Assert.Equal(WorkDisposition.Execute, security.Disposition);
                Assert.Equal(PlanningReason.EvidenceChanged, security.PlanningReason);
            },
            change => Assert.Equal(WorkDisposition.Reuse, change.Disposition));
    }

    [Fact]
    public void Reaching_one_deadline_executes_only_the_expired_branch()
    {
        var previous = PassingResults().ToDictionary(result => result.Check);
        previous[ReadinessCheck.Change] = Result(
            ReadinessCheck.Change,
            BranchOutcome.Passed,
            ChangeEvidenceId,
            Now);

        var work = Planner().Plan(Request(previous.Values));

        Assert.Collection(
            work,
            test => Assert.Equal(WorkDisposition.Reuse, test.Disposition),
            security => Assert.Equal(WorkDisposition.Reuse, security.Disposition),
            change =>
            {
                Assert.Equal(WorkDisposition.Execute, change.Disposition);
                Assert.Equal(PlanningReason.Expired, change.PlanningReason);
            });
    }

    public static TheoryData<bool, bool, BranchOutcome, bool, PlanningReason>
        PrioritizedPlanningCases() => new()
        {
            { true, true, BranchOutcome.Blocked, false, PlanningReason.ExplicitlySelected },
            { false, true, BranchOutcome.Blocked, false, PlanningReason.EvidenceChanged },
            { false, false, BranchOutcome.Blocked, false, PlanningReason.PreviousResultNotPassed },
            { false, false, BranchOutcome.Passed, true, PlanningReason.Expired },
            { false, false, BranchOutcome.Passed, false, PlanningReason.StillCurrent },
        };

    private static RoundPlanner Planner() => new(new FixedTimeProvider(Now.Value));

    private static RoundPlanningRequest Request(
        IEnumerable<BranchResult> previousResults,
        IReadOnlyDictionary<ReadinessCheck, Guid>? currentEvidence = null,
        IEnumerable<ReadinessCheck>? explicitlySelected = null) => new(
        Id,
        roundNumber: previousResults.Any() ? 2 : 1,
        previousResults,
        currentEvidence ?? CurrentEvidence(),
        explicitlySelected ?? []);

    private static BranchResult[] PassingResults() =>
    [
        Result(ReadinessCheck.Test, BranchOutcome.Passed, TestEvidenceId, Utc(2026, 8, 17, 11)),
        Result(ReadinessCheck.Security, BranchOutcome.Passed, SecurityEvidenceId, Utc(2026, 8, 17, 12)),
        Result(ReadinessCheck.Change, BranchOutcome.Passed, ChangeEvidenceId, Utc(2026, 8, 17, 13)),
    ];

    private static Dictionary<ReadinessCheck, Guid> CurrentEvidence() => new()
    {
        [ReadinessCheck.Test] = TestEvidenceId,
        [ReadinessCheck.Security] = SecurityEvidenceId,
        [ReadinessCheck.Change] = ChangeEvidenceId,
    };

    private static BranchResult Result(
        ReadinessCheck check,
        BranchOutcome outcome,
        Guid evidenceId,
        UtcInstant? validUntil) => new(
        Guid.NewGuid(),
        Id,
        roundNumber: 1,
        check,
        outcome,
        ExecutionDisposition.Executed,
        PlanningReason.InitialEvaluation,
        "Executed because this was the initial evaluation.",
        evidenceId,
        EvidenceKindFor(check),
        validUntil,
        ["Attempt 1 succeeded."],
        new Dictionary<string, string> { ["ready"] = "Policy result." },
        reuseSourceResultId: null,
        reuseSourceRound: null);

    private static EvidenceKind EvidenceKindFor(ReadinessCheck check) => check switch
    {
        ReadinessCheck.Test => EvidenceKind.Test,
        ReadinessCheck.Security => EvidenceKind.Security,
        ReadinessCheck.Change => EvidenceKind.Change,
        _ => throw new ArgumentOutOfRangeException(nameof(check)),
    };

    private static readonly Guid TestEvidenceId = new("11111111-1111-1111-1111-111111111111");
    private static readonly Guid SecurityEvidenceId = new("22222222-2222-2222-2222-222222222222");
    private static readonly Guid ChangeEvidenceId = new("33333333-3333-3333-3333-333333333333");

    private static UtcInstant Utc(int year, int month, int day, int hour) =>
        new(new DateTimeOffset(year, month, day, hour, 0, 0, TimeSpan.Zero));

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}

public sealed class SelectiveRerunReuseTests
{
    private static readonly ReleaseId Id = new("release-42");
    private static readonly UtcInstant Now = Utc(2026, 8, 17, 10);
    private static readonly UtcInstant ValidUntil = Utc(2026, 8, 17, 11);

    [Fact]
    public async Task Planned_result_identities_survive_execution_and_reuse_unchanged()
    {
        var evidence = Evidence();
        var executedResultId = Guid.NewGuid();
        var provider = new CountingProvider(evidence);
        var policy = new CountingPolicy();
        var executed = await BranchExecution.ExecuteAsync(
            executedResultId,
            Submission(),
            new BranchWorkItem(
                Id,
                roundNumber: 2,
                ReadinessCheck.Test,
                WorkDisposition.Execute,
                PlanningReason.ExplicitlySelected,
                "Executed because Test was explicitly selected for rerun."),
            ReadinessCheck.Test,
            EvidenceKind.Test,
            provider,
            policy.Evaluate,
            CancellationToken.None);
        var source = SourceResult(evidence.Id);
        var reusedResultId = Guid.NewGuid();
        var reused = Reuse().Create(
            reusedResultId,
            ReuseWorkItem(source),
            source,
            evidence.Id);

        Assert.Equal(executedResultId, executed.Id);
        Assert.Equal(reusedResultId, reused.Id);
    }

    [Fact]
    public void Reuse_emits_a_new_result_linked_to_the_verified_source_without_execution_attempts()
    {
        var source = SourceResult();
        var workItem = ReuseWorkItem(source);

        var reused = Reuse().Create(
            Guid.NewGuid(),
            workItem,
            source,
            source.EvidenceId);

        Assert.NotEqual(source.Id, reused.Id);
        Assert.Equal(2, reused.RoundNumber);
        Assert.Equal(ExecutionDisposition.Reused, reused.Disposition);
        Assert.Equal(source.Id, reused.ReuseSourceResultId);
        Assert.Equal(source.RoundNumber, reused.ReuseSourceRound);
        Assert.Equal(source.EvidenceId, reused.EvidenceId);
        Assert.Equal(source.ValidUntil, reused.ValidUntil);
        Assert.Empty(reused.Attempts);
        Assert.Equal(source.Findings, reused.Findings);
        Assert.Contains("safe", reused.PlanningDetail, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("evidence")]
    [InlineData("deadline")]
    public void Defensive_reuse_failure_is_a_technical_failure(string mismatch)
    {
        var source = SourceResult(validUntil: mismatch == "deadline" ? Now : ValidUntil);
        var evidenceId = mismatch == "evidence" ? Guid.NewGuid() : source.EvidenceId;

        Assert.Throws<InvalidOperationException>(() => Reuse().Create(
            Guid.NewGuid(),
            ReuseWorkItem(source),
            source,
            evidenceId));
    }

    [Fact]
    public async Task Reuse_makes_zero_provider_or_policy_calls_while_execution_calls_each_once()
    {
        var evidence = Evidence();
        var provider = new CountingProvider(evidence);
        var policy = new CountingPolicy();
        var source = SourceResult(evidence.Id);

        _ = Reuse().Create(
            Guid.NewGuid(),
            ReuseWorkItem(source),
            source,
            evidence.Id);

        Assert.Equal(0, provider.CallCount);
        Assert.Equal(0, policy.CallCount);

        var executeWork = new BranchWorkItem(
            Id,
            roundNumber: 2,
            ReadinessCheck.Test,
            WorkDisposition.Execute,
            PlanningReason.ExplicitlySelected,
            "Executed because Test was explicitly selected for rerun.");
        _ = await BranchExecution.ExecuteAsync(
            Guid.NewGuid(),
            Submission(),
            executeWork,
            ReadinessCheck.Test,
            EvidenceKind.Test,
            provider,
            policy.Evaluate,
            CancellationToken.None);

        Assert.Equal(1, provider.CallCount);
        Assert.Equal(1, policy.CallCount);
    }

    private static ResultReuse Reuse() => new(new FixedTimeProvider(Now.Value));

    private static BranchWorkItem ReuseWorkItem(BranchResult source) => new(
        Id,
        roundNumber: 2,
        ReadinessCheck.Test,
        WorkDisposition.Reuse,
        PlanningReason.StillCurrent,
        "Reused from round 1 because the result is safe to reuse.",
        source.Id);

    private static BranchResult SourceResult(
        Guid? evidenceId = null,
        UtcInstant? validUntil = null) => new(
        Guid.NewGuid(),
        Id,
        roundNumber: 1,
        ReadinessCheck.Test,
        BranchOutcome.Passed,
        ExecutionDisposition.Executed,
        PlanningReason.InitialEvaluation,
        "Executed because this was the initial evaluation.",
        evidenceId ?? Evidence().Id,
        EvidenceKind.Test,
        validUntil ?? ValidUntil,
        ["Attempt 1 succeeded."],
        new Dictionary<string, string> { ["ready"] = "Test evidence passed." },
        reuseSourceResultId: null,
        reuseSourceRound: null);

    private static TestEvidenceRecord Evidence() => new(
        Guid.NewGuid(),
        Id,
        version: 1,
        recordedAt: Utc(2026, 8, 17, 9),
        supersedesEvidenceId: null,
        testRunVersion: "2.4.0",
        completedAt: Utc(2026, 8, 17, 9),
        passRate: 0.98m,
        criticalSuiteFailures: []);

    private static ReleaseSubmission Submission() => new(
        Id,
        "orders",
        "2.4.0",
        new UtcInterval(Utc(2026, 8, 17, 10), Utc(2026, 8, 17, 11)),
        Utc(2026, 8, 17, 8));

    private static UtcInstant Utc(int year, int month, int day, int hour) =>
        new(new DateTimeOffset(year, month, day, hour, 0, 0, TimeSpan.Zero));

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class CountingProvider(TestEvidenceRecord evidence) : ITestEvidenceProvider
    {
        public int CallCount { get; private set; }

        public ValueTask<TestEvidenceRecord?> GetCurrentAsync(
            ReleaseId releaseRevision,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return ValueTask.FromResult<TestEvidenceRecord?>(evidence);
        }
    }

    private sealed class CountingPolicy : ITestReadinessPolicy
    {
        public int CallCount { get; private set; }

        public TestPolicyEvaluation Evaluate(
            ReleaseSubmission submission,
            TestEvidenceRecord evidence)
        {
            CallCount++;
            return new TestPolicyEvaluation(
                BranchOutcome.Passed,
                ValidUntil,
                new Dictionary<string, string> { ["ready"] = "Test evidence passed." });
        }
    }
}
