using ReleaseReadinessCoordinator.Domain;
using ReleaseReadinessCoordinator.Data;
using ReleaseReadinessCoordinator.Tests.Support;
using ReleaseReadinessCoordinator.Workflow;

namespace ReleaseReadinessCoordinator.Tests.Workflow;

public sealed class WorkflowScenarioTests
{
    [Fact]
    public async Task All_pass_round_waits_for_approval_and_approves_once()
    {
        await using var scenario = await ScenarioBuilder.CreateAsync("scenario-all-pass");
        await using var application = scenario.CreateApplication();

        var wait = Assert.IsType<PendingApprovalWait>(await application.StartAsync());
        var waiting = await application.GetDetailAsync();

        Assert.Equal(ProcessPhase.WaitingForApproval, waiting.Release.Phase);
        Assert.Equal(scenario.Now, Assert.Single(waiting.HumanDecisionRequests).CreatedAt);
        AssertCompleteRound(waiting, roundNumber: 1);
        AssertTimelineExplains(waiting, waiting.EvaluationRounds[0]);
        scenario.Calls.AssertCounts(test: (1, 1), security: (1, 1), change: (1, 1));

        var response = scenario.ApprovalResponse(HumanDecision.Approve);
        await application.ResumeApprovalAsync(response);
        await application.ResumeApprovalAsync(response);

        var approved = await application.GetDetailAsync();
        Assert.Equal(ProcessPhase.Approved, approved.Release.Phase);
        Assert.Equal(response.Response, Assert.IsType<PersistedHumanResponse>(approved.TerminalResponse).Response);
        Assert.Single(approved.Timeline.Where(entry => entry.Kind is TimelineEntryKind.HumanResponseAccepted));
        Assert.Equal(wait.WorkflowRequestId, waiting.WorkflowCorrelation?.PendingWorkflowRequestId);
        scenario.Calls.AssertCounts(test: (1, 1), security: (1, 1), change: (1, 1));
    }

    [Fact]
    public async Task Multi_block_remediation_executes_affected_checks_and_reuses_the_other()
    {
        await using var scenario = await ScenarioBuilder.CreateAsync(
            "scenario-multi-block",
            test: BranchOutcome.Blocked,
            security: BranchOutcome.Blocked);
        await using var application = scenario.CreateApplication();

        var wait = Assert.IsType<PendingRemediationWait>(await application.StartAsync());
        var blocked = await application.GetDetailAsync();

        Assert.Equal(ProcessPhase.WaitingForRemediation, blocked.Release.Phase);
        Assert.Equal(scenario.Now, Assert.Single(blocked.RemediationRequests).CreatedAt);
        Assert.Equal(
            [ReadinessCheck.Test, ReadinessCheck.Security],
            blocked.RemediationRequests[0].Problems.Select(result => result.Check));
        AssertCompleteRound(blocked, roundNumber: 1);
        AssertTimelineExplains(blocked, blocked.EvaluationRounds[0]);
        scenario.Calls.AssertCounts(test: (1, 1), security: (1, 1), change: (1, 1));
        var aggregationReplay = await application.ReplayAggregationAsync(blocked.EvaluationRounds[0]);
        Assert.Equal(blocked.RemediationRequests[0].Id, aggregationReplay.RemediationRequest?.Id);

        scenario.Clock.Advance(TimeSpan.FromMinutes(15));
        var response = await scenario.RemediationResponseAsync(
            wait,
            [ReadinessCheck.Test, ReadinessCheck.Security],
            [ReadinessCheck.Test]);
        Assert.False(await application.SubmitRemediationAsync(
            "mismatched-correlation",
            response.EvidenceReplacements,
            [ReadinessCheck.Test]));
        Assert.Empty((await application.GetDetailAsync()).RemediationSubmissions);
        await application.ResumeRemediationAsync(response);
        await application.ResumeRemediationAsync(response);

        var ready = await application.GetDetailAsync();
        Assert.Equal(ProcessPhase.WaitingForApproval, ready.Release.Phase);
        Assert.Equal(new UtcInstant(scenario.Clock.GetUtcNow()), Assert.Single(ready.HumanDecisionRequests).CreatedAt);
        AssertCompleteRound(ready, roundNumber: 2);
        AssertTimelineExplains(ready, ready.EvaluationRounds[1]);
        Assert.Collection(
            ready.EvaluationRounds[1].Results,
            test =>
            {
                Assert.Equal(ExecutionDisposition.Executed, test.Disposition);
                Assert.Equal(PlanningReason.ExplicitlySelected, test.PlanningReason);
            },
            security =>
            {
                Assert.Equal(ExecutionDisposition.Executed, security.Disposition);
                Assert.Equal(PlanningReason.EvidenceChanged, security.PlanningReason);
            },
            change =>
            {
                Assert.Equal(ExecutionDisposition.Reused, change.Disposition);
                Assert.Equal(PlanningReason.StillCurrent, change.PlanningReason);
                Assert.Equal(1, change.ReuseSourceRound);
            });
        Assert.Single(ready.RemediationSubmissions);
        Assert.Equal(2, ready.EvaluationRounds.Length);
        scenario.Calls.AssertCounts(test: (2, 2), security: (2, 2), change: (1, 1));
    }

    [Fact]
    public async Task Missing_and_transient_results_fan_in_before_one_remediation_wait_and_resume()
    {
        await using var scenario = await ScenarioBuilder.CreateAsync(
            "scenario-missing-transient",
            test: BranchOutcome.MissingEvidence,
            security: BranchOutcome.TransientFailure);
        await using var application = scenario.CreateApplication();

        var wait = Assert.IsType<PendingRemediationWait>(await application.StartAsync());
        var waiting = await application.GetDetailAsync();
        var round = Assert.Single(waiting.EvaluationRounds);

        Assert.Equal(ProcessPhase.WaitingForRemediation, waiting.Release.Phase);
        Assert.Equal(scenario.Now, Assert.Single(waiting.RemediationRequests).CreatedAt);
        Assert.Equal(
            [BranchOutcome.MissingEvidence, BranchOutcome.TransientFailure, BranchOutcome.Passed],
            round.Results.Select(result => result.Outcome));
        Assert.Equal(3, round.Results[1].Attempts.Length);
        AssertCompleteRound(waiting, roundNumber: 1);
        AssertTimelineExplains(waiting, round);
        scenario.Calls.AssertCounts(test: (1, 0), security: (3, 0), change: (1, 1));

        scenario.Clock.Advance(TimeSpan.FromMinutes(10));
        var response = await scenario.RemediationResponseAsync(
            wait,
            [ReadinessCheck.Test, ReadinessCheck.Security]);
        await application.ResumeRemediationAsync(response);
        await application.ResumeRemediationAsync(response);

        var ready = await application.GetDetailAsync();
        Assert.Equal(ProcessPhase.WaitingForApproval, ready.Release.Phase);
        Assert.All(ready.EvaluationRounds[1].Results, result => Assert.Equal(BranchOutcome.Passed, result.Outcome));
        Assert.Equal(ExecutionDisposition.Reused, ready.EvaluationRounds[1].Results[2].Disposition);
        AssertCompleteRound(ready, roundNumber: 2);
        AssertTimelineExplains(ready, ready.EvaluationRounds[1]);
        Assert.Single(ready.RemediationSubmissions);
        scenario.Calls.AssertCounts(test: (2, 1), security: (4, 1), change: (1, 1));
    }

    [Fact]
    public async Task Remediation_wait_survives_restart_and_resumes_once()
    {
        await using var scenario = await ScenarioBuilder.CreateAsync(
            "scenario-remediation-restart",
            test: BranchOutcome.Blocked);
        PendingRemediationWait started;

        await using (var firstApplication = scenario.CreateApplication())
        {
            started = Assert.IsType<PendingRemediationWait>(await firstApplication.StartAsync());
            var beforeRestart = await firstApplication.GetDetailAsync();
            Assert.Equal(ProcessPhase.WaitingForRemediation, beforeRestart.Release.Phase);
            Assert.Equal(scenario.Now, Assert.Single(beforeRestart.RemediationRequests).CreatedAt);
            AssertCompleteRound(beforeRestart, roundNumber: 1);
            AssertTimelineExplains(beforeRestart, beforeRestart.EvaluationRounds[0]);
        }

        await using var restartedApplication = scenario.CreateApplication();
        var restored = Assert.IsType<PendingRemediationWait>(await restartedApplication.RestoreAsync());
        Assert.Equal(started, restored);
        await using var concurrentApplication = restartedApplication.CreatePeer();

        scenario.Clock.Advance(TimeSpan.FromMinutes(5));
        var response = await scenario.RemediationResponseAsync(
            restored,
            [ReadinessCheck.Test]);
        await Task.WhenAll(
            restartedApplication.ResumeRemediationAsync(response),
            concurrentApplication.ResumeRemediationAsync(response));

        var resumed = Assert.IsType<PendingApprovalWait>(await restartedApplication.RestoreAsync());
        var detail = await restartedApplication.GetDetailAsync();
        Assert.Equal(ProcessPhase.WaitingForApproval, detail.Release.Phase);
        Assert.Equal(resumed.WorkflowRequestId, detail.WorkflowCorrelation?.PendingWorkflowRequestId);
        Assert.Single(detail.RemediationSubmissions);
        Assert.Equal(2, detail.EvaluationRounds.Length);
        AssertCompleteRound(detail, roundNumber: 2);
        AssertTimelineExplains(detail, detail.EvaluationRounds[1]);
        scenario.Calls.AssertCounts(test: (2, 2), security: (1, 1), change: (1, 1));
    }

    [Fact]
    public async Task Approval_wait_survives_restart_and_approves_once()
    {
        await using var scenario = await ScenarioBuilder.CreateAsync("scenario-approval-restart");
        PendingApprovalWait started;

        await using (var firstApplication = scenario.CreateApplication())
        {
            started = Assert.IsType<PendingApprovalWait>(await firstApplication.StartAsync());
            var beforeRestart = await firstApplication.GetDetailAsync();
            Assert.Equal(ProcessPhase.WaitingForApproval, beforeRestart.Release.Phase);
            Assert.Equal(scenario.Now, Assert.Single(beforeRestart.HumanDecisionRequests).CreatedAt);
            AssertCompleteRound(beforeRestart, roundNumber: 1);
            AssertTimelineExplains(beforeRestart, beforeRestart.EvaluationRounds[0]);
        }

        await using var restartedApplication = scenario.CreateApplication();
        var restored = Assert.IsType<PendingApprovalWait>(await restartedApplication.RestoreAsync());
        Assert.Equal(started.WorkflowRequestId, restored.WorkflowRequestId);
        Assert.Equal(started.ApprovalRequestId, restored.ApprovalRequestId);
        await using var concurrentApplication = restartedApplication.CreatePeer();

        var response = scenario.ApprovalResponse(HumanDecision.Approve);
        await Task.WhenAll(
            restartedApplication.ResumeApprovalAsync(response),
            concurrentApplication.ResumeApprovalAsync(response));

        var detail = await restartedApplication.GetDetailAsync();
        Assert.Equal(ProcessPhase.Approved, detail.Release.Phase);
        Assert.Single(detail.HumanDecisionRequests);
        Assert.Single(detail.Timeline.Where(entry => entry.Kind is TimelineEntryKind.HumanResponseAccepted));
        Assert.Equal(response.Response, detail.TerminalResponse?.Response);
        scenario.Calls.AssertCounts(test: (1, 1), security: (1, 1), change: (1, 1));
    }

    [Fact]
    public async Task Restored_approval_rejects_once_and_terminal_release_cannot_reopen()
    {
        await using var scenario = await ScenarioBuilder.CreateAsync("scenario-terminal-rejection");

        await using (var firstApplication = scenario.CreateApplication())
        {
            Assert.IsType<PendingApprovalWait>(await firstApplication.StartAsync());
        }

        await using var restartedApplication = scenario.CreateApplication();
        var restored = Assert.IsType<PendingApprovalWait>(await restartedApplication.RestoreAsync());
        var responseId = Guid.NewGuid();
        var waiting = await restartedApplication.GetDetailAsync();
        Assert.Equal(scenario.Now, Assert.Single(waiting.HumanDecisionRequests).CreatedAt);

        Assert.True(await restartedApplication.SubmitDecisionAsync(
            responseId,
            HumanDecision.Reject,
            "release-manager",
            "The release remains too risky."));
        Assert.True(await restartedApplication.SubmitDecisionAsync(
            responseId,
            HumanDecision.Reject,
            "release-manager",
            "The release remains too risky."));
        Assert.False(await restartedApplication.SubmitDecisionAsync(
            responseId,
            HumanDecision.Approve,
            "release-manager",
            "Attempt to reopen."));

        var rejected = await restartedApplication.GetDetailAsync();
        Assert.Equal(ProcessPhase.Rejected, rejected.Release.Phase);
        Assert.Equal(restored.WorkflowRequestId, rejected.WorkflowCorrelation?.PendingWorkflowRequestId);
        Assert.Equal(HumanDecision.Reject, rejected.TerminalResponse?.Response.Decision);
        Assert.Single(rejected.Timeline.Where(entry => entry.Kind is TimelineEntryKind.HumanResponseAccepted));
        AssertCompleteRound(rejected, roundNumber: 1);
        AssertTimelineExplains(rejected, rejected.EvaluationRounds[0]);
        scenario.Calls.AssertCounts(test: (1, 1), security: (1, 1), change: (1, 1));
    }

    [Fact]
    public async Task Unexpected_provider_exception_is_a_visible_technical_failure()
    {
        await using var scenario = await ScenarioBuilder.CreateAsync("scenario-technical-failure");
        scenario.Calls.FailUnexpectedly(ReadinessCheck.Security);
        await using var application = scenario.CreateApplication();

        await Assert.ThrowsAnyAsync<Exception>(application.StartAsync);

        var failed = await application.GetDetailAsync();
        Assert.Equal(ProcessPhase.Failed, failed.Release.Phase);
        Assert.Empty(failed.EvaluationRounds);
        Assert.Empty(failed.RemediationRequests);
        Assert.Empty(failed.HumanDecisionRequests);
        Assert.Null(failed.TerminalResponse);
        Assert.Equal((1, 0), scenario.Calls.CountsFor(ReadinessCheck.Security));
        var failure = Assert.Single(failed.Timeline.Where(entry => entry.Kind is TimelineEntryKind.WorkflowFailed));
        Assert.StartsWith("Workflow failed (", failure.Summary, StringComparison.Ordinal);
        Assert.DoesNotContain(BranchOutcome.TransientFailure.ToString(), failure.Summary, StringComparison.Ordinal);
    }

    private static void AssertCompleteRound(ReleaseDetailProjection detail, int roundNumber)
    {
        var round = Assert.Single(detail.EvaluationRounds.Where(item => item.RoundNumber == roundNumber));
        Assert.Equal(Enum.GetValues<ReadinessCheck>(), round.Results.Select(result => result.Check));
    }

    private static void AssertTimelineExplains(
        ReleaseDetailProjection detail,
        EvaluationRound round)
    {
        var timeline = Assert.Single(detail.Timeline.Where(entry =>
            entry.Kind is TimelineEntryKind.EvaluationCompleted
            && entry.Summary.StartsWith($"Round {round.RoundNumber} completed.", StringComparison.Ordinal)));
        Assert.All(round.Results, result => Assert.Contains(result.PlanningDetail, timeline.Summary, StringComparison.Ordinal));
    }
}
