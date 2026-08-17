using Microsoft.Agents.AI.Workflows;
using ReleaseReadinessCoordinator.Tests.Readiness;
using ReleaseReadinessCoordinator.Workflow;
using BranchOutcome = ReleaseReadinessCoordinator.Domain.BranchOutcome;

namespace ReleaseReadinessCoordinator.Tests.Workflow;

public sealed class ExternalRequestContractTests
{
    [Theory]
    [InlineData(BranchOutcome.Blocked, ReleaseWorkflowPortIds.Remediation)]
    [InlineData(BranchOutcome.MissingEvidence, ReleaseWorkflowPortIds.Remediation)]
    [InlineData(BranchOutcome.TransientFailure, ReleaseWorkflowPortIds.Remediation)]
    [InlineData(BranchOutcome.Passed, ReleaseWorkflowPortIds.Approval)]
    public async Task Aggregate_outcome_selects_typed_request_after_complete_fan_in(
        BranchOutcome testOutcome,
        string expectedPortId)
    {
        var input = new EvaluationRoundPlan(
            RoundNumber: 11,
            Test: new BranchPlan(BranchDisposition.Execute, testOutcome),
            Security: new BranchPlan(BranchDisposition.Execute, BranchOutcome.Passed),
            Change: new BranchPlan(BranchDisposition.Execute, BranchOutcome.Passed));
        var workflow = ReadinessWorkflowTestFactory.CreateForPlan(input);

        await using var run = await InProcessExecution.RunAsync(workflow, input);
        var events = run.NewEvents.ToArray();

        var requestIndex = Array.FindIndex(events, workflowEvent => workflowEvent is RequestInfoEvent);
        Assert.True(requestIndex >= 0);

        var completionsBeforeRequest = events
            .Take(requestIndex)
            .OfType<ExecutorCompletedEvent>()
            .Select(completion => completion.ExecutorId)
            .ToArray();

        Assert.All(
            ReleaseWorkflowExecutorIds.Branches.Values,
            executorId => Assert.Contains(executorId, completionsBeforeRequest));
        Assert.Contains(ReleaseWorkflowExecutorIds.Aggregator, completionsBeforeRequest);

        var request = Assert.Single(events.OfType<RequestInfoEvent>()).Request;
        Assert.Equal(expectedPortId, request.PortInfo.PortId);

        if (testOutcome is not BranchOutcome.Passed)
        {
            Assert.True(request.TryGetDataAs<RemediationRequest>(out var remediation));
            Assert.Equal(11, remediation.RoundNumber);
            Assert.Throws<InvalidOperationException>(
                () => request.CreateResponse(new ApprovalResponse(Approved: true)));
        }
        else
        {
            Assert.True(request.TryGetDataAs<ApprovalRequest>(out var approval));
            Assert.Equal(11, approval.RoundNumber);
            Assert.Throws<InvalidOperationException>(
                () => request.CreateResponse(new RemediationResponse(input with { RoundNumber = 12 })));
        }
    }
}
