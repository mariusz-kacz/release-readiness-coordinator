using Microsoft.Agents.AI.Workflows;
using ReleaseReadinessCoordinator.Domain;
using ReleaseReadinessCoordinator.Readiness;

namespace ReleaseReadinessCoordinator.Workflow;

internal sealed class ReadinessWorkflowDependencies
{
    public ReadinessWorkflowDependencies(
        ITestEvidenceProvider testEvidenceProvider,
        ITestReadinessPolicy testPolicy,
        ISecurityEvidenceProvider securityEvidenceProvider,
        ISecurityReadinessPolicy securityPolicy,
        IChangeEvidenceProvider changeEvidenceProvider,
        IChangeReadinessPolicy changePolicy)
    {
        TestEvidenceProvider = testEvidenceProvider ??
            throw new ArgumentNullException(nameof(testEvidenceProvider));
        TestPolicy = testPolicy ?? throw new ArgumentNullException(nameof(testPolicy));
        SecurityEvidenceProvider = securityEvidenceProvider ??
            throw new ArgumentNullException(nameof(securityEvidenceProvider));
        SecurityPolicy = securityPolicy ?? throw new ArgumentNullException(nameof(securityPolicy));
        ChangeEvidenceProvider = changeEvidenceProvider ??
            throw new ArgumentNullException(nameof(changeEvidenceProvider));
        ChangePolicy = changePolicy ?? throw new ArgumentNullException(nameof(changePolicy));
    }

    public ITestEvidenceProvider TestEvidenceProvider { get; }

    public ITestReadinessPolicy TestPolicy { get; }

    public ISecurityEvidenceProvider SecurityEvidenceProvider { get; }

    public ISecurityReadinessPolicy SecurityPolicy { get; }

    public IChangeEvidenceProvider ChangeEvidenceProvider { get; }

    public IChangeReadinessPolicy ChangePolicy { get; }
}

public static class ReleaseWorkflowFactory
{
    internal static Microsoft.Agents.AI.Workflows.Workflow Create(
        ReleaseSubmission submission,
        ReadinessWorkflowDependencies dependencies)
    {
        ArgumentNullException.ThrowIfNull(dependencies);
        return Build(
            new ReadinessBranchExecutor(
                submission,
                dependencies.TestEvidenceProvider,
                dependencies.TestPolicy),
            new ReadinessBranchExecutor(
                submission,
                dependencies.SecurityEvidenceProvider,
                dependencies.SecurityPolicy),
            new ReadinessBranchExecutor(
                submission,
                dependencies.ChangeEvidenceProvider,
                dependencies.ChangePolicy));
    }

    private static Microsoft.Agents.AI.Workflows.Workflow Build(
        ReadinessBranchExecutor test,
        ReadinessBranchExecutor security,
        ReadinessBranchExecutor change)
    {
        var planner = new ReadinessPlanner();
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
