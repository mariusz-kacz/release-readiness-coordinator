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
        var remediation = RequestPort.Create<RemediationRequest, RemediationResponse>(
            ReleaseWorkflowPortIds.Remediation);
        var approval = RequestPort.Create<ApprovalRequest, ApprovalResponse>(
            ReleaseWorkflowPortIds.Approval);
        var approvalCompletion = new ApprovalCompletionExecutor();

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
        builder.AddEdge(aggregator, remediation);
        builder.AddEdge(aggregator, approval);
        builder.AddEdge(remediation, planner);
        builder.AddEdge(approval, approvalCompletion);
        builder.WithOutputFrom(aggregator, approvalCompletion);

        return builder.Build();
    }
}
