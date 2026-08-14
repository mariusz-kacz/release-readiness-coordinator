using Microsoft.Agents.AI.Workflows;
using ReleaseReadinessCoordinator.Workflow;

namespace ReleaseReadinessCoordinator.Tests.Workflow;

public sealed class ExternalRequestContractTests
{
    [Theory]
    [InlineData(ExternalWaitKind.Remediation, ReleaseWorkflowPortIds.Remediation)]
    [InlineData(ExternalWaitKind.Approval, ReleaseWorkflowPortIds.Approval)]
    public async Task Typed_request_is_emitted_only_after_complete_fan_in(
        ExternalWaitKind waitKind,
        string expectedPortId)
    {
        var workflow = ReleaseWorkflowFactory.Create();
        var input = new EvaluationRoundPlan(
            RoundNumber: 11,
            Test: BranchDisposition.Execute,
            Security: BranchDisposition.Execute,
            Change: BranchDisposition.Execute,
            Dependency: BranchDisposition.Execute,
            WaitKind: waitKind);

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

        if (waitKind is ExternalWaitKind.Remediation)
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
