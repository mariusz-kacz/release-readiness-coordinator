using Microsoft.Agents.AI.Workflows;
using ReleaseReadinessCoordinator.Tests.Readiness;
using ReleaseReadinessCoordinator.Workflow;
using ReleaseReadinessCoordinator.Domain;

namespace ReleaseReadinessCoordinator.Tests.Workflow;

public sealed class CheckpointContractTests
{
    [Fact]
    public void Coordinator_results_expose_only_consumed_state()
    {
        Assert.Equal(
            [nameof(WorkflowStartResult.PendingRequest)],
            typeof(WorkflowStartResult).GetProperties().Select(property => property.Name).Order());
        Assert.Equal(
            [nameof(WorkflowRemediationResumeResult.NextRequest)],
            typeof(WorkflowRemediationResumeResult).GetProperties().Select(property => property.Name).Order());
    }

    [Fact]
    public async Task Pending_request_is_rehydrated_after_process_boundary_and_accepts_correlated_response()
    {
        await using var host = await ReadinessWorkflowTestHost.CreateForOutcomesAsync(
            BranchOutcome.Passed);
        using var directory = new TemporaryDirectory();
        const string SessionId = "release-42-revision-3";
        WorkflowStartResult started;

        using (var firstProcess = new CheckpointStoreCoordinator(directory.Info))
        {
            started = await firstProcess.StartAsync(
                host.CreateWorkflow(),
                host.Input,
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
            host.CreateWorkflow(),
            SessionId,
            started.PendingRequest,
            new ApprovalResponse(Approved: true));

        Assert.Equal(started.PendingRequest, resumed.RestoredRequest);
        Assert.True(resumed.Output.Approved);
    }

    [Fact]
    public async Task Mismatched_request_is_rejected_without_consuming_the_pending_wait()
    {
        await using var host = await ReadinessWorkflowTestHost.CreateForOutcomesAsync(
            BranchOutcome.Passed);
        using var directory = new TemporaryDirectory();
        const string SessionId = "release-7-revision-1";
        WorkflowStartResult started;

        using (var firstProcess = new CheckpointStoreCoordinator(directory.Info))
        {
            started = await firstProcess.StartAsync(
                host.CreateWorkflow(),
                host.Input,
                SessionId);
        }

        using var secondProcess = new CheckpointStoreCoordinator(directory.Info);
        var mismatch = started.PendingRequest with { RequestId = Guid.NewGuid().ToString("N") };

        var exception = await Assert.ThrowsAsync<WorkflowContinuationException>(
            () => secondProcess.ResumeApprovalAsync(
                host.CreateWorkflow(),
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
                host.CreateWorkflow(),
                SessionId,
                wrongType,
                new ApprovalResponse(Approved: true)));

        Assert.Equal(ContinuationFailureKind.Mismatched, exception.Kind);

        var resumed = await secondProcess.ResumeApprovalAsync(
            host.CreateWorkflow(),
            SessionId,
            started.PendingRequest,
            new ApprovalResponse(Approved: true));
        Assert.True(resumed.Output.Approved);
    }

    [Fact]
    public async Task Missing_continuation_state_is_a_visible_technical_failure()
    {
        await using var host = await ReadinessWorkflowTestHost.CreateForOutcomesAsync(
            BranchOutcome.Passed);
        using var directory = new TemporaryDirectory();
        using var coordinator = new CheckpointStoreCoordinator(directory.Info);

        var exception = await Assert.ThrowsAsync<WorkflowContinuationException>(
            () => coordinator.ResumeApprovalAsync(
                host.CreateWorkflow(),
                "missing-session",
                new PendingWorkflowRequest("missing-request", ExternalWaitKind.Approval, ReleaseWorkflowPortIds.Approval),
                new ApprovalResponse(Approved: true)));

        Assert.Equal(ContinuationFailureKind.Missing, exception.Kind);
    }

    [Fact]
    public async Task Corrupt_continuation_state_is_a_visible_technical_failure()
    {
        await using var host = await ReadinessWorkflowTestHost.CreateForOutcomesAsync(
            BranchOutcome.Passed);
        using var directory = new TemporaryDirectory();
        const string SessionId = "release-corrupt";
        WorkflowStartResult started;

        using (var firstProcess = new CheckpointStoreCoordinator(directory.Info))
        {
            started = await firstProcess.StartAsync(
                host.CreateWorkflow(),
                host.Input,
                SessionId);
        }

        var checkpointFile = Directory
            .EnumerateFiles(directory.Info.FullName, "*", SearchOption.AllDirectories)
            .Single(path => File.ReadAllText(path).Contains(started.PendingRequest.RequestId, StringComparison.Ordinal));
        await File.WriteAllTextAsync(checkpointFile, "{not-valid-json");

        using var secondProcess = new CheckpointStoreCoordinator(directory.Info);
        var exception = await Assert.ThrowsAsync<WorkflowContinuationException>(
            () => secondProcess.ResumeApprovalAsync(
                host.CreateWorkflow(),
                SessionId,
                started.PendingRequest,
                new ApprovalResponse(Approved: true)));

        Assert.Equal(ContinuationFailureKind.Corrupt, exception.Kind);
    }

    [Fact]
    public async Task Incompatible_rebuilt_graph_is_a_visible_technical_failure()
    {
        await using var host = await ReadinessWorkflowTestHost.CreateForOutcomesAsync(
            BranchOutcome.Passed);
        using var directory = new TemporaryDirectory();
        const string SessionId = "release-incompatible";
        WorkflowStartResult started;

        using (var firstProcess = new CheckpointStoreCoordinator(directory.Info))
        {
            started = await firstProcess.StartAsync(
                host.CreateWorkflow(),
                host.Input,
                SessionId);
        }

        var incompatibleExecutor = new ApprovalCompletionExecutor();
        var incompatibleWorkflow = new WorkflowBuilder(incompatibleExecutor)
            .WithOutputFrom(incompatibleExecutor)
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
