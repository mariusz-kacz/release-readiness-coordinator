using Microsoft.Agents.AI.Workflows;
using ReleaseReadinessCoordinator.Workflow;

namespace ReleaseReadinessCoordinator.Tests.Workflow;

public sealed class WorkflowTopologyTests
{
    [Fact]
    public void Aggregator_rejects_duplicate_branch_results()
    {
        var results = CompleteResults()
            .Select(result => result.Branch == ReadinessBranch.Dependency
                ? result with { Branch = ReadinessBranch.Test, ExecutorId = ReleaseWorkflowExecutorIds.Test }
                : result)
            .ToArray();

        Assert.Throws<InvalidOperationException>(() => ReadinessAggregator.Aggregate(results));
    }

    [Fact]
    public void Aggregator_rejects_omitted_branch_results()
    {
        var results = CompleteResults()
            .Where(result => result.Branch != ReadinessBranch.Dependency)
            .ToArray();

        Assert.Throws<InvalidOperationException>(() => ReadinessAggregator.Aggregate(results));
    }

    [Fact]
    public void Aggregator_rejects_impossible_branch_identities()
    {
        var results = CompleteResults();
        results[3] = results[3] with { Branch = (ReadinessBranch)999 };

        Assert.Throws<InvalidOperationException>(() => ReadinessAggregator.Aggregate(results));
    }

    [Fact]
    public async Task Real_graph_routes_once_per_branch_before_aggregation()
    {
        var workflow = ReleaseWorkflowFactory.Create();
        var input = new EvaluationRoundPlan(
            RoundNumber: 7,
            Test: BranchDisposition.Execute,
            Security: BranchDisposition.Reuse,
            Change: BranchDisposition.Execute,
            Dependency: BranchDisposition.Reuse,
            WaitKind: ExternalWaitKind.Approval);

        await using var run = await InProcessExecution.RunAsync(workflow, input);
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

        var output = Assert.Single(events.OfType<WorkflowOutputEvent>());
        var round = Assert.IsType<EvaluationRoundResult>(output.Data);
        Assert.Equal(7, round.RoundNumber);
        Assert.Equal(
            new[]
            {
                (ReadinessBranch.Test, BranchDisposition.Execute),
                (ReadinessBranch.Security, BranchDisposition.Reuse),
                (ReadinessBranch.Change, BranchDisposition.Execute),
                (ReadinessBranch.Dependency, BranchDisposition.Reuse),
            },
            round.Results.Select(result => (result.Branch, result.Disposition)));
    }

    private static BranchResult[] CompleteResults() =>
    [
        new(3, ReadinessBranch.Test, BranchDisposition.Execute, ReleaseWorkflowExecutorIds.Test, ExternalWaitKind.Approval),
        new(3, ReadinessBranch.Security, BranchDisposition.Execute, ReleaseWorkflowExecutorIds.Security, ExternalWaitKind.Approval),
        new(3, ReadinessBranch.Change, BranchDisposition.Execute, ReleaseWorkflowExecutorIds.Change, ExternalWaitKind.Approval),
        new(3, ReadinessBranch.Dependency, BranchDisposition.Execute, ReleaseWorkflowExecutorIds.Dependency, ExternalWaitKind.Approval),
    ];
}
