using Microsoft.Agents.AI.Workflows;
using ReleaseReadinessCoordinator.Domain;
using ReleaseReadinessCoordinator.Tests.Readiness;
using ReleaseReadinessCoordinator.Workflow;

namespace ReleaseReadinessCoordinator.Tests.Workflow;

public sealed class WorkflowTopologyTests
{
    [Fact]
    public async Task Real_graph_routes_once_per_branch_before_aggregation()
    {
        await using var host = await ReadinessWorkflowTestHost.CreateForOutcomesAsync(
            BranchOutcome.Passed);

        await using var run = await InProcessExecution.RunAsync(
            host.CreateWorkflow(),
            host.Input);
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

        Assert.DoesNotContain(
            events.OfType<WorkflowOutputEvent>(),
            output => output.Data is EvaluationRound);
        var detail = await host.DataService.GetReleaseDetailAsync(host.Submission.ReleaseId);
        var round = Assert.Single(detail!.EvaluationRounds);
        Assert.Equal(host.Input.RoundId, round.Id);
        Assert.Equal(1, round.RoundNumber);
        Assert.Equal(
            new[]
            {
                (ReadinessCheck.Test, ExecutionDisposition.Executed),
                (ReadinessCheck.Security, ExecutionDisposition.Executed),
                (ReadinessCheck.Change, ExecutionDisposition.Executed),
            },
            round.Results.Select(result => (result.Check, result.Disposition)));
    }

}
