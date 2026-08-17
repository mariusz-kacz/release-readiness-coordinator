using Microsoft.Agents.AI.Workflows;
using ReleaseReadinessCoordinator.Tests.Readiness;
using ReleaseReadinessCoordinator.Workflow;
using BranchOutcome = ReleaseReadinessCoordinator.Domain.BranchOutcome;

namespace ReleaseReadinessCoordinator.Tests.Workflow;

public sealed class CheckpointContractTests
{
    [Fact]
    public async Task Pending_request_is_rehydrated_after_process_boundary_and_accepts_correlated_response()
    {
        using var directory = new TemporaryDirectory();
        const string SessionId = "release-42-revision-3";
        WorkflowStartResult started;

        using (var firstProcess = new CheckpointStoreCoordinator(directory.Info))
        {
            started = await firstProcess.StartAsync(
                PassingWorkflow(),
                PassingPlan(),
                SessionId);

            Assert.Equal(ExternalWaitKind.Approval, started.PendingRequest.Kind);
            Assert.Equal(ReleaseWorkflowPortIds.Approval, started.PendingRequest.PortId);
            Assert.False(string.IsNullOrWhiteSpace(started.PendingRequest.RequestId));
        }

        var persistedJson = Directory
            .EnumerateFiles(directory.Info.FullName, "*", SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .ToArray();
        Assert.NotEmpty(persistedJson);
        Assert.Contains(persistedJson, json => json.Contains(started.PendingRequest.RequestId, StringComparison.Ordinal));

        using var secondProcess = new CheckpointStoreCoordinator(directory.Info);
        var resumed = await secondProcess.ResumeApprovalAsync(
            PassingWorkflow(),
            SessionId,
            started.PendingRequest,
            new ApprovalResponse(Approved: true));

        Assert.Equal(started.PendingRequest, resumed.RestoredRequest);
        Assert.True(resumed.Output.Approved);
    }

    [Fact]
    public async Task Mismatched_request_is_rejected_without_consuming_the_pending_wait()
    {
        using var directory = new TemporaryDirectory();
        const string SessionId = "release-7-revision-1";
        WorkflowStartResult started;

        using (var firstProcess = new CheckpointStoreCoordinator(directory.Info))
        {
            started = await firstProcess.StartAsync(
                PassingWorkflow(),
                PassingPlan(),
                SessionId);
        }

        using var secondProcess = new CheckpointStoreCoordinator(directory.Info);
        var mismatch = started.PendingRequest with { RequestId = Guid.NewGuid().ToString("N") };

        var exception = await Assert.ThrowsAsync<WorkflowContinuationException>(
            () => secondProcess.ResumeApprovalAsync(
                PassingWorkflow(),
                SessionId,
                mismatch,
                new ApprovalResponse(Approved: true)));

        Assert.Equal(ContinuationFailureKind.Mismatched, exception.Kind);

        var wrongType = started.PendingRequest with
        {
            Kind = ExternalWaitKind.Remediation,
            PortId = ReleaseWorkflowPortIds.Remediation,
        };
        exception = await Assert.ThrowsAsync<WorkflowContinuationException>(
            () => secondProcess.ResumeApprovalAsync(
                PassingWorkflow(),
                SessionId,
                wrongType,
                new ApprovalResponse(Approved: true)));

        Assert.Equal(ContinuationFailureKind.Mismatched, exception.Kind);

        var resumed = await secondProcess.ResumeApprovalAsync(
            PassingWorkflow(),
            SessionId,
            started.PendingRequest,
            new ApprovalResponse(Approved: true));
        Assert.True(resumed.Output.Approved);
    }

    [Fact]
    public async Task Missing_continuation_state_is_a_visible_technical_failure()
    {
        using var directory = new TemporaryDirectory();
        using var coordinator = new CheckpointStoreCoordinator(directory.Info);

        var exception = await Assert.ThrowsAsync<WorkflowContinuationException>(
            () => coordinator.ResumeApprovalAsync(
                PassingWorkflow(),
                "missing-session",
                new PendingWorkflowRequest("missing-request", ExternalWaitKind.Approval, ReleaseWorkflowPortIds.Approval),
                new ApprovalResponse(Approved: true)));

        Assert.Equal(ContinuationFailureKind.Missing, exception.Kind);
    }

    [Fact]
    public async Task Corrupt_continuation_state_is_a_visible_technical_failure()
    {
        using var directory = new TemporaryDirectory();
        const string SessionId = "release-corrupt";
        WorkflowStartResult started;

        using (var firstProcess = new CheckpointStoreCoordinator(directory.Info))
        {
            started = await firstProcess.StartAsync(
                PassingWorkflow(),
                PassingPlan(),
                SessionId);
        }

        var checkpointFile = Directory
            .EnumerateFiles(directory.Info.FullName, "*", SearchOption.AllDirectories)
            .Single(path => File.ReadAllText(path).Contains(started.PendingRequest.RequestId, StringComparison.Ordinal));
        await File.WriteAllTextAsync(checkpointFile, "{not-valid-json");

        using var secondProcess = new CheckpointStoreCoordinator(directory.Info);
        var exception = await Assert.ThrowsAsync<WorkflowContinuationException>(
            () => secondProcess.ResumeApprovalAsync(
                PassingWorkflow(),
                SessionId,
                started.PendingRequest,
                new ApprovalResponse(Approved: true)));

        Assert.Equal(ContinuationFailureKind.Corrupt, exception.Kind);
    }

    [Fact]
    public async Task Incompatible_rebuilt_graph_is_a_visible_technical_failure()
    {
        using var directory = new TemporaryDirectory();
        const string SessionId = "release-incompatible";
        WorkflowStartResult started;

        using (var firstProcess = new CheckpointStoreCoordinator(directory.Info))
        {
            started = await firstProcess.StartAsync(
                PassingWorkflow(),
                PassingPlan(),
                SessionId);
        }

        var incompatiblePlanner = new ReadinessPlanner();
        var incompatibleWorkflow = new WorkflowBuilder(incompatiblePlanner)
            .WithOutputFrom(incompatiblePlanner)
            .Build();

        using var secondProcess = new CheckpointStoreCoordinator(directory.Info);
        var exception = await Assert.ThrowsAsync<WorkflowContinuationException>(
            () => secondProcess.ResumeApprovalAsync(
                incompatibleWorkflow,
                SessionId,
                started.PendingRequest,
                new ApprovalResponse(Approved: true)));

        Assert.Equal(ContinuationFailureKind.Incompatible, exception.Kind);
    }

    private static EvaluationRoundPlan PassingPlan() => new(
        RoundNumber: 3,
        Test: new BranchPlan(BranchDisposition.Execute, BranchOutcome.Passed),
        Security: new BranchPlan(BranchDisposition.Execute, BranchOutcome.Passed),
        Change: new BranchPlan(BranchDisposition.Execute, BranchOutcome.Passed));

    private static Microsoft.Agents.AI.Workflows.Workflow PassingWorkflow() =>
        ReadinessWorkflowTestFactory.CreateForPlan(PassingPlan());

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Info = Directory.CreateDirectory(
                Path.Combine(Path.GetTempPath(), "release-readiness-tests", Guid.NewGuid().ToString("N")));
        }

        public DirectoryInfo Info { get; }

        public void Dispose()
        {
            if (Info.Exists)
            {
                Info.Delete(recursive: true);
            }
        }
    }
}
