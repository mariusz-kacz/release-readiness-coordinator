using ReleaseReadinessCoordinator.Data;
using ReleaseReadinessCoordinator.Domain;

namespace ReleaseReadinessCoordinator.Workflow;

internal static class WorkflowWaitResolver
{
    public static PendingWorkflowWait? Resolve(ReleaseDetailProjection detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        return detail.WorkflowCorrelation is { } correlation
            ? Resolve(detail, correlation)
            : null;
    }

    public static PendingWorkflowWait Require(ReleaseDetailProjection detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        var correlation = detail.WorkflowCorrelation
            ?? throw new InvalidOperationException(
                $"Release '{detail.Release.Submission.ReleaseId}' has no workflow correlation.");
        return Resolve(detail, correlation)
            ?? throw new InvalidOperationException(
                $"Release phase '{detail.Release.Phase}' does not match correlated request kind '{correlation.PendingRequestKind}'.");
    }

    private static PendingWorkflowWait? Resolve(
        ReleaseDetailProjection detail,
        WorkflowCorrelationRecord correlation)
    {
        PendingWorkflowWait? wait = (detail.Release.Phase, correlation.PendingRequestKind) switch
        {
            (ProcessPhase.WaitingForRemediation, WorkflowRequestKind.Remediation) =>
                new PendingRemediationWait(
                    correlation.PendingWorkflowRequestId,
                    ActiveRemediationRequest(detail).Id),
            (ProcessPhase.WaitingForApproval, WorkflowRequestKind.Approval) =>
                new PendingApprovalWait(
                    correlation.PendingWorkflowRequestId,
                    RequireApproval(detail).Request.Id),
            _ => null,
        };
        return wait?.DomainRequestId == correlation.PendingDomainRequestId
            ? wait
            : null;
    }

    private static RemediationRequest ActiveRemediationRequest(ReleaseDetailProjection detail)
    {
        var answeredRequestIds = detail.RemediationSubmissions
            .Select(submission => submission.RequestId)
            .ToHashSet();
        return detail.RemediationRequests.Single(request => !answeredRequestIds.Contains(request.Id));
    }

    public static ApprovalRequest RequireApproval(ReleaseDetailProjection detail)
    {
        ArgumentNullException.ThrowIfNull(detail);
        var request = detail.HumanDecisionRequests.Single();
        var snapshot = detail.DecisionSnapshots.Single(value => value.Id == request.SnapshotId);
        return new ApprovalRequest(snapshot, request);
    }
}
