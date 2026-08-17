using Microsoft.Agents.AI.Workflows;
using ReleaseReadinessCoordinator.Domain;
using ReleaseReadinessCoordinator.Readiness;

namespace ReleaseReadinessCoordinator.Workflow;

public static class ReleaseWorkflowFactory
{
    public static Microsoft.Agents.AI.Workflows.Workflow Create() =>
        Create(
            new ReadinessBranchExecutor(
                ReadinessBranch.Test,
                ReleaseWorkflowExecutorIds.Test),
            new ReadinessBranchExecutor(
                ReadinessBranch.Security,
                ReleaseWorkflowExecutorIds.Security));

    internal static Microsoft.Agents.AI.Workflows.Workflow Create(
        ReleaseSubmission submission,
        ITestEvidenceProvider testEvidenceProvider,
        ITestReadinessPolicy testPolicy) =>
        Create(
            new ReadinessBranchExecutor(
                submission,
                testEvidenceProvider,
                testPolicy),
            new ReadinessBranchExecutor(
                ReadinessBranch.Security,
                ReleaseWorkflowExecutorIds.Security));

    internal static Microsoft.Agents.AI.Workflows.Workflow Create(
        ReleaseSubmission submission,
        ITestEvidenceProvider testEvidenceProvider,
        ITestReadinessPolicy testPolicy,
        ISecurityEvidenceProvider securityEvidenceProvider,
        ISecurityReadinessPolicy securityPolicy) =>
        Create(
            new ReadinessBranchExecutor(
                submission,
                testEvidenceProvider,
                testPolicy),
            new ReadinessBranchExecutor(
                submission,
                securityEvidenceProvider,
                securityPolicy));

    private static Microsoft.Agents.AI.Workflows.Workflow Create(
        ReadinessBranchExecutor test,
        ReadinessBranchExecutor security)
    {
        var planner = new ReadinessPlanner();
        var change = new ReadinessBranchExecutor(
            ReadinessBranch.Change,
            ReleaseWorkflowExecutorIds.Change);
        var aggregator = new ReadinessAggregator();
        var remediation = RequestPort.Create<RemediationRequest, RemediationResponse>(
            ReleaseWorkflowPortIds.Remediation);
        var approval = RequestPort.Create<ApprovalRequest, ApprovalResponse>(
            ReleaseWorkflowPortIds.Approval);
        var approvalCompletion = new ApprovalCompletionExecutor();

        ExecutorBinding[] branchBindings = [test, security, change];

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
        builder.AddFanInBarrierEdge(branchBindings, aggregator);
        builder.AddEdge(aggregator, remediation);
        builder.AddEdge(aggregator, approval);
        builder.AddEdge(remediation, planner);
        builder.AddEdge(approval, approvalCompletion);
        builder.WithOutputFrom(aggregator, approvalCompletion);

        return builder.Build();
    }
}
