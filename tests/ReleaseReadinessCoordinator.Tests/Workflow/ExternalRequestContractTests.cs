using Microsoft.Agents.AI.Workflows;
using ReleaseReadinessCoordinator.Domain;
using ReleaseReadinessCoordinator.Tests.Readiness;
using ReleaseReadinessCoordinator.Workflow;

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
        await using var host = await ReadinessWorkflowTestHost.CreateForOutcomesAsync(testOutcome);

        await using var run = await InProcessExecution.RunAsync(
            host.CreateWorkflow(),
            host.Input);
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
            Assert.True(request.TryGetDataAs<RemediationWaitReference>(out var remediation));
            var detail = await host.DataService.GetReleaseDetailAsync(host.Submission.ReleaseId);
            Assert.Equal(Assert.Single(detail!.RemediationRequests).Id, remediation.RequestId);
            Assert.Throws<InvalidOperationException>(
                () => request.CreateResponse(Approval()));
        }
        else
        {
            Assert.True(request.TryGetDataAs<ApprovalWaitReference>(out var approval));
            var detail = await host.DataService.GetReleaseDetailAsync(host.Submission.ReleaseId);
            var persistedApproval = WorkflowWaitResolver.RequireApproval(detail!);
            Assert.Equal(persistedApproval.Request.Id, approval.RequestId);
            Assert.Equal(1, persistedApproval.RoundNumber);
            Assert.Equal(persistedApproval.Snapshot.Id, persistedApproval.Request.SnapshotId);
            Assert.Equal(host.Submission.ReleaseId, persistedApproval.Snapshot.ReleaseId);
            Assert.Equal(3, persistedApproval.Snapshot.Sources.Length);
            Assert.False(string.IsNullOrWhiteSpace(persistedApproval.Snapshot.DecisionBrief));
            Assert.Throws<InvalidOperationException>(
                () => request.CreateResponse(new RemediationWorkflowResponse(
                    new RemediationSubmission(
                        Guid.NewGuid(),
                        Guid.NewGuid(),
                        host.Input.StartedAt,
                        new Dictionary<EvidenceKind, Guid>(),
                        []),
                    [])));
        }
    }

    private static ApprovalResponse Approval() => new(new HumanResponse(
        Guid.NewGuid(),
        HumanDecision.Approve,
        "release-manager",
        "Reviewed.",
        new UtcInstant(DateTimeOffset.UtcNow)));
}
