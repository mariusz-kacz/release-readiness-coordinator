using Microsoft.Agents.AI.Workflows;
using ReleaseReadinessCoordinator.Data;
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
        IApplicationDataService dataService,
        TimeProvider timeProvider,
        ReadinessWorkflowDependencies dependencies)
    {
        ArgumentNullException.ThrowIfNull(submission);
        ArgumentNullException.ThrowIfNull(dataService);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(dependencies);

        var planner = new ReadinessPlanner(
            submission.ReleaseId,
            dataService,
            timeProvider);
        var test = new ReadinessBranchExecutor(
            submission,
            dependencies.TestEvidenceProvider,
            dependencies.TestPolicy,
            timeProvider);
        var security = new ReadinessBranchExecutor(
            submission,
            dependencies.SecurityEvidenceProvider,
            dependencies.SecurityPolicy,
            timeProvider);
        var change = new ReadinessBranchExecutor(
            submission,
            dependencies.ChangeEvidenceProvider,
            dependencies.ChangePolicy,
            timeProvider);
        var aggregator = new ReadinessAggregator(dataService, timeProvider);
        var snapshotBuilder = new DecisionSnapshotWorkflowExecutor(
            submission.ReleaseId,
            dataService,
            timeProvider);
        var remediation = RequestPort.Create<RemediationWaitReference, RemediationWorkflowResponse>(
            ReleaseWorkflowPortIds.Remediation);
        var remediationHandler = new RemediationWorkflowExecutor(submission.ReleaseId, dataService);
        var approval = RequestPort.Create<ApprovalWaitReference, ApprovalResponse>(
            ReleaseWorkflowPortIds.Approval);
        var humanDecisionHandler = new HumanDecisionWorkflowExecutor(
            submission.ReleaseId,
            dataService);
        var approvalCompletion = new ApprovalCompletionExecutor();

        ExecutorBinding[] branchBindings = [test, security, change];
        var builder = new WorkflowBuilder(planner);
        builder.AddEdge<PlannedBranchWorkItem>(
            planner,
            test,
            item => item is not null && item.WorkItem.Check is ReadinessCheck.Test);
        builder.AddEdge<PlannedBranchWorkItem>(
            planner,
            security,
            item => item is not null && item.WorkItem.Check is ReadinessCheck.Security);
        builder.AddEdge<PlannedBranchWorkItem>(
            planner,
            change,
            item => item is not null && item.WorkItem.Check is ReadinessCheck.Change);
        builder.AddFanInBarrierEdge(branchBindings, aggregator);
        builder.AddEdge(aggregator, remediation);
        builder.AddEdge(aggregator, snapshotBuilder);
        builder.AddEdge(snapshotBuilder, approval);
        builder.AddEdge(remediation, remediationHandler);
        builder.AddEdge(remediationHandler, planner);
        builder.AddEdge(approval, humanDecisionHandler);
        builder.AddEdge(humanDecisionHandler, approvalCompletion);
        builder.WithOutputFrom(approvalCompletion);
        return builder.Build();
    }

}
