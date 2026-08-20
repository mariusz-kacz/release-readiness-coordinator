using ReleaseReadinessCoordinator.Domain;
using ReleaseReadinessCoordinator.Readiness;
using ReleaseReadinessCoordinator.Workflow;
using BranchResult = ReleaseReadinessCoordinator.Domain.BranchResult;
using BranchWorkItem = ReleaseReadinessCoordinator.Domain.BranchWorkItem;

namespace ReleaseReadinessCoordinator.Tests.Workflow;

public sealed class SelectiveRerunPlanningTests
{
    private static readonly ReleaseId Id = new("release-42");
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
        PlanningReason expectedReason)
    {
        var source = Result(
            ReadinessCheck.Test,
            previousOutcome,
            evidenceId: TestEvidenceId);
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
            expectedReason is PlanningReason.UnchangedEvidence ? WorkDisposition.Reuse : WorkDisposition.Execute,
            item.Disposition);
        Assert.Equal(expectedReason, item.PlanningReason);
        Assert.Equal(
            expectedReason is PlanningReason.UnchangedEvidence ? source.Id : null,
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
            test =>
            {
                Assert.Equal(WorkDisposition.Reuse, test.Disposition);
                Assert.Equal(
                    "Reused from round 1 because the exact evidence is unchanged and the previous result passed.",
                    test.PlanningDetail);
            },
            security =>
            {
                Assert.Equal(WorkDisposition.Execute, security.Disposition);
                Assert.Equal(PlanningReason.EvidenceChanged, security.PlanningReason);
            },
            change => Assert.Equal(WorkDisposition.Reuse, change.Disposition));
    }

    [Fact]
    public void Newly_available_evidence_reason_explains_the_change_without_internal_identifiers()
    {
        var previous = PassingResults().ToDictionary(result => result.Check);
        previous[ReadinessCheck.Test] = Result(
            ReadinessCheck.Test,
            BranchOutcome.MissingEvidence,
            evidenceId: null);

        var item = Assert.Single(
            Planner().Plan(Request(previous.Values)),
            item => item.Check is ReadinessCheck.Test);

        Assert.Equal(PlanningReason.EvidenceChanged, item.PlanningReason);
        Assert.Equal(
            "Executed because Test evidence is now available. The previous result had no Test evidence. Additional facts: previous result did not pass.",
            item.PlanningDetail);
    }

    [Fact]
    public void Unchanged_passing_results_are_reused_without_elapsed_time_as_an_input()
    {
        var work = Planner().Plan(Request(PassingResults()));

        Assert.All(work, item =>
        {
            Assert.Equal(WorkDisposition.Reuse, item.Disposition);
            Assert.Equal(PlanningReason.UnchangedEvidence, item.PlanningReason);
        });
    }

    public static TheoryData<bool, bool, BranchOutcome, PlanningReason>
        PrioritizedPlanningCases() => new()
        {
            { true, true, BranchOutcome.Blocked, PlanningReason.ExplicitlySelected },
            { false, true, BranchOutcome.Blocked, PlanningReason.EvidenceChanged },
            { false, false, BranchOutcome.Blocked, PlanningReason.PreviousResultNotPassed },
            { false, false, BranchOutcome.Passed, PlanningReason.UnchangedEvidence },
        };

    private static RoundPlanner Planner() => new();

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
        Result(ReadinessCheck.Test, BranchOutcome.Passed, TestEvidenceId),
        Result(ReadinessCheck.Security, BranchOutcome.Passed, SecurityEvidenceId),
        Result(ReadinessCheck.Change, BranchOutcome.Passed, ChangeEvidenceId),
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
        Guid? evidenceId) => new(
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

}

public sealed class SelectiveRerunReuseTests
{
    private static readonly ReleaseId Id = new("release-42");
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
        Assert.Empty(reused.Attempts);
        Assert.Equal(source.Findings, reused.Findings);
        Assert.Equal(
            "Reused from round 1 because the exact evidence is unchanged and the previous result passed.",
            reused.PlanningDetail);
    }

    [Fact]
    public void Defensive_evidence_identity_mismatch_is_a_technical_failure()
    {
        var source = SourceResult();

        Assert.Throws<InvalidOperationException>(() => Reuse().Create(
            Guid.NewGuid(),
            ReuseWorkItem(source),
            source,
            Guid.NewGuid()));
    }

    [Theory]
    [InlineData("release")]
    [InlineData("branch")]
    [InlineData("round")]
    [InlineData("outcome")]
    [InlineData("linkage")]
    public void Defensive_source_mismatch_is_a_technical_failure(string mismatch)
    {
        var source = mismatch switch
        {
            "release" => SourceResult(releaseId: new ReleaseId("another-release")),
            "branch" => SourceResult(check: ReadinessCheck.Security),
            "round" => SourceResult(roundNumber: 2),
            "outcome" => SourceResult(outcome: BranchOutcome.Blocked),
            _ => SourceResult(),
        };
        var workItem = ReuseWorkItem(
            source,
            mismatch == "linkage" ? Guid.NewGuid() : source.Id);

        Assert.Throws<InvalidOperationException>(() => Reuse().Create(
            Guid.NewGuid(),
            workItem,
            source,
            source.EvidenceId));
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

    private static ResultReuse Reuse() => new();

    private static BranchWorkItem ReuseWorkItem(
        BranchResult source,
        Guid? sourceResultId = null) => new(
        Id,
        roundNumber: 2,
        ReadinessCheck.Test,
        WorkDisposition.Reuse,
        PlanningReason.UnchangedEvidence,
        "Reused from round 1 because the result is safe to reuse.",
        sourceResultId ?? source.Id);

    private static BranchResult SourceResult(
        Guid? evidenceId = null,
        ReleaseId? releaseId = null,
        ReadinessCheck check = ReadinessCheck.Test,
        int roundNumber = 1,
        BranchOutcome outcome = BranchOutcome.Passed) => new(
        Guid.NewGuid(),
        releaseId ?? Id,
        roundNumber,
        check,
        outcome,
        ExecutionDisposition.Executed,
        PlanningReason.InitialEvaluation,
        "Executed because this was the initial evaluation.",
        evidenceId ?? Evidence().Id,
        (EvidenceKind)(int)check,
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
                new Dictionary<string, string> { ["ready"] = "Test evidence passed." });
        }
    }
}
