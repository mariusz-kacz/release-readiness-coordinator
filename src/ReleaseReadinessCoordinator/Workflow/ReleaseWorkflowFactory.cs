using Microsoft.Agents.AI.Workflows;

namespace ReleaseReadinessCoordinator.Workflow;

public static class ReleaseWorkflowFactory
{
    public static Microsoft.Agents.AI.Workflows.Workflow Create()
    {
        var planner = new ReadinessPlanner();
        var test = new ReadinessBranchExecutor(
            ReadinessBranch.Test,
            ReleaseWorkflowExecutorIds.Test);
        var security = new ReadinessBranchExecutor(
            ReadinessBranch.Security,
            ReleaseWorkflowExecutorIds.Security);
        var change = new ReadinessBranchExecutor(
            ReadinessBranch.Change,
            ReleaseWorkflowExecutorIds.Change);
        var dependency = new ReadinessBranchExecutor(
            ReadinessBranch.Dependency,
            ReleaseWorkflowExecutorIds.Dependency);
        var aggregator = new ReadinessAggregator();

        ExecutorBinding[] branchBindings = [test, security, change, dependency];

        var builder = new WorkflowBuilder(planner);
        builder.AddEdge<BranchWorkItem>(
            planner,
            test,
            workItem => workItem is { Branch: ReadinessBranch.Test });
        builder.AddEdge<BranchWorkItem>(
            planner,
            security,
            workItem => workItem is { Branch: ReadinessBranch.Security });
        builder.AddEdge<BranchWorkItem>(
            planner,
            change,
            workItem => workItem is { Branch: ReadinessBranch.Change });
        builder.AddEdge<BranchWorkItem>(
            planner,
            dependency,
            workItem => workItem is { Branch: ReadinessBranch.Dependency });
        builder.AddFanInBarrierEdge(branchBindings, aggregator);
        builder.WithOutputFrom(aggregator);

        return builder.Build();
    }
}
