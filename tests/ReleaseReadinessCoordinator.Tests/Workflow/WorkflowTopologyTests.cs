using Microsoft.Agents.AI.Workflows;
using ReleaseReadinessCoordinator.Domain;
using ReleaseReadinessCoordinator.Tests.Readiness;
using ReleaseReadinessCoordinator.Workflow;
using System.Reflection;

namespace ReleaseReadinessCoordinator.Tests.Workflow;

public sealed class WorkflowTopologyTests
{
    [Fact]
    public void Workflow_contract_has_one_factory_and_canonical_executor_types()
    {
        var factoryMethods = typeof(ReleaseWorkflowFactory)
            .GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Where(method => method.Name == "Create")
            .ToArray();
        var workflowAssembly = typeof(ReleaseWorkflowFactory).Assembly;

        Assert.Single(factoryMethods);
        Assert.Equal(4, factoryMethods[0].GetParameters().Length);
        Assert.Null(workflowAssembly.GetType("ReleaseReadinessCoordinator.Workflow.EvaluationRoundPlan"));
        Assert.Null(workflowAssembly.GetType("ReleaseReadinessCoordinator.Workflow.BranchPlan"));
        Assert.NotNull(workflowAssembly.GetType("ReleaseReadinessCoordinator.Workflow.ReadinessPlanner"));
        Assert.NotNull(workflowAssembly.GetType("ReleaseReadinessCoordinator.Workflow.ReadinessBranchExecutor"));
        Assert.NotNull(workflowAssembly.GetType("ReleaseReadinessCoordinator.Workflow.ReadinessAggregator"));
        Assert.NotNull(workflowAssembly.GetType("ReleaseReadinessCoordinator.Workflow.RemediationWorkflowExecutor"));
        Assert.Null(workflowAssembly.GetType("ReleaseReadinessCoordinator.Workflow.PersistentReadinessPlanner"));
        Assert.Null(workflowAssembly.GetType("ReleaseReadinessCoordinator.Workflow.PersistentReadinessBranchExecutor"));
        Assert.Null(workflowAssembly.GetType("ReleaseReadinessCoordinator.Workflow.PersistentReadinessAggregator"));
        Assert.Null(workflowAssembly.GetType("ReleaseReadinessCoordinator.Workflow.PersistentRemediationHandler"));
    }

    [Fact]
    public void Round_messages_do_not_preselect_the_external_wait()
    {
        Type[] roundMessageTypes =
        [
            typeof(EvaluationRoundStart),
            typeof(PlannedBranchWorkItem),
            typeof(CompletedBranchWork),
            typeof(EvaluationRound),
        ];

        Assert.All(
            roundMessageTypes,
            messageType => Assert.DoesNotContain(
                messageType.GetProperties(),
                property => typeof(PendingWorkflowRequest).IsAssignableFrom(property.PropertyType)));
    }

    [Fact]
    public void Workflow_exposes_exactly_three_readiness_branches()
    {
        Assert.Equal(
            [ReadinessBranch.Test, ReadinessBranch.Security, ReadinessBranch.Change],
            ReleaseWorkflowExecutorIds.Branches.Keys);
    }

    [Fact]
    public void Durable_round_contract_rejects_duplicate_check_results()
    {
        var results = CompleteResults();
        results[2] = Result(ReadinessCheck.Test);

        Assert.Throws<InvalidOperationException>(() => Round(results));
    }

    [Fact]
    public void Durable_round_contract_rejects_omitted_check_results()
    {
        var results = CompleteResults()
            .Where(result => result.Check is not ReadinessCheck.Change);

        Assert.Throws<InvalidOperationException>(() => Round(results));
    }

    [Fact]
    public void Durable_result_contract_rejects_impossible_check_identity()
    {
        Assert.ThrowsAny<ArgumentException>(() => Result((ReadinessCheck)999));
    }

    [Fact]
    public async Task Real_graph_routes_once_per_branch_before_aggregation()
    {
        await using var host = await ReadinessWorkflowTestHost.CreateForOutcomesAsync(
            BranchOutcome.Passed);

        await using var run = await InProcessExecution.RunAsync(
            host.CreateWorkflow(),
            host.Input);
        var events = run.NewEvents.ToArray();
        var completions = events.OfType<ExecutorCompletedEvent>().ToArray();
        var branchExecutorIds = ReleaseWorkflowExecutorIds.Branches.Values.ToArray();

        foreach (var executorId in branchExecutorIds)
        {
            Assert.Single(completions, completion => completion.ExecutorId == executorId);
        }

        var aggregationIndex = Array.FindIndex(
            completions,
            completion => completion.ExecutorId == ReleaseWorkflowExecutorIds.Aggregator);
        Assert.True(aggregationIndex >= 0);
        Assert.All(
            branchExecutorIds,
            executorId => Assert.True(
                Array.FindIndex(completions, completion => completion.ExecutorId == executorId) < aggregationIndex));

        var requestIndex = Array.FindIndex(events, workflowEvent => workflowEvent is RequestInfoEvent);
        var aggregationEventIndex = Array.FindIndex(
            events,
            workflowEvent => workflowEvent is ExecutorCompletedEvent completion
                && completion.ExecutorId == ReleaseWorkflowExecutorIds.Aggregator);
        Assert.True(requestIndex > aggregationEventIndex);

        Assert.DoesNotContain(
            events.OfType<WorkflowOutputEvent>(),
            output => output.Data is EvaluationRound);
        var detail = await host.DataService.GetReleaseDetailAsync(host.Submission.Key);
        var round = Assert.Single(detail!.EvaluationRounds);
        Assert.Equal(1, round.RoundNumber);
        Assert.Equal(
            new[]
            {
                (ReadinessCheck.Test, ExecutionDisposition.Executed),
                (ReadinessCheck.Security, ExecutionDisposition.Executed),
                (ReadinessCheck.Change, ExecutionDisposition.Executed),
            },
            round.Results.Select(result => (result.Check, result.Disposition)));
    }

    private static EvaluationRound Round(IEnumerable<BranchResult> results) => new(
        Guid.NewGuid(),
        new ReleaseRevisionKey("topology-contract", 1),
        1,
        Utc(10),
        Utc(10, 5),
        results);

    private static BranchResult[] CompleteResults() =>
    [
        Result(ReadinessCheck.Test),
        Result(ReadinessCheck.Security),
        Result(ReadinessCheck.Change),
    ];

    private static BranchResult Result(ReadinessCheck check) => new(
        Guid.NewGuid(),
        new ReleaseRevisionKey("topology-contract", 1),
        1,
        check,
        BranchOutcome.Passed,
        ExecutionDisposition.Executed,
        PlanningReason.InitialEvaluation,
        "Executed because this is the initial evaluation.",
        Guid.NewGuid(),
        check is ReadinessCheck.Security
            ? EvidenceKind.Security
            : check is ReadinessCheck.Change
                ? EvidenceKind.Change
                : EvidenceKind.Test,
        Utc(11),
        ["Attempt 1 succeeded."],
        new Dictionary<string, string> { ["ready"] = "true" },
        null,
        null);

    private static UtcInstant Utc(int hour, int minute = 0) =>
        new(new DateTimeOffset(2026, 8, 17, hour, minute, 0, TimeSpan.Zero));
}
