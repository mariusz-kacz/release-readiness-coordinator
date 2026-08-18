using Microsoft.Agents.AI.Workflows;
using ReleaseReadinessCoordinator.Tests.Readiness;
using ReleaseReadinessCoordinator.Workflow;
using ReleaseReadinessCoordinator.Domain;

namespace ReleaseReadinessCoordinator.Tests.Workflow;

public sealed class CheckpointContractTests
{
    [Fact]
    public void Coordinator_exposes_typed_pending_requests()
    {
        Assert.True(typeof(PendingWorkflowRequest).IsAbstract);
        Assert.True(typeof(PendingRemediationRequest).IsSealed);
        Assert.True(typeof(PendingApprovalRequest).IsSealed);
        Assert.Equal(
            [nameof(PendingRemediationRequest.DomainRequestId), nameof(PendingWorkflowRequest.RequestId)],
            typeof(PendingRemediationRequest).GetProperties().Select(property => property.Name).Order());
        Assert.Equal(
            [nameof(PendingWorkflowRequest.RequestId)],
            typeof(PendingApprovalRequest).GetProperties().Select(property => property.Name).Order());
    }

    [Fact]
    public void Coordinator_actions_accept_only_their_valid_request_types()
    {
        var remediation = typeof(CheckpointStoreCoordinator)
            .GetMethod(nameof(CheckpointStoreCoordinator.ResumeRemediationAsync))!;
        var approval = typeof(CheckpointStoreCoordinator)
            .GetMethod(nameof(CheckpointStoreCoordinator.ResumeApprovalAsync))!;

        Assert.Equal(typeof(PendingRemediationRequest), remediation.GetParameters()[2].ParameterType);
        Assert.Equal(typeof(Task<PendingWorkflowRequest>), remediation.ReturnType);
        Assert.Equal(typeof(PendingApprovalRequest), approval.GetParameters()[2].ParameterType);
        Assert.Equal(typeof(Task<ApprovalResponse>), approval.ReturnType);
    }

    [Fact]
    public async Task Pending_request_is_rehydrated_after_process_boundary_and_accepts_correlated_response()
    {
        await using var host = await ReadinessWorkflowTestHost.CreateForOutcomesAsync(
            BranchOutcome.Passed);
        using var directory = new TemporaryDirectory();
        const string SessionId = "release-42-revision-3";
        PendingApprovalRequest started;

        using (var firstProcess = new CheckpointStoreCoordinator(directory.Info))
        {
            started = Assert.IsType<PendingApprovalRequest>(
                await firstProcess.StartAsync(
                    host.CreateWorkflow(),
                    host.Input,
                    SessionId));

            Assert.False(string.IsNullOrWhiteSpace(started.RequestId));
        }

        var persistedJson = Directory
            .EnumerateFiles(directory.Info.FullName, "*", SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .ToArray();
        Assert.NotEmpty(persistedJson);
        Assert.Contains(persistedJson, json => json.Contains(started.RequestId, StringComparison.Ordinal));

        using var secondProcess = new CheckpointStoreCoordinator(directory.Info);
        var resumed = await secondProcess.ResumeApprovalAsync(
            host.CreateWorkflow(),
            SessionId,
            started,
            new ApprovalResponse(Approved: true));

        Assert.True(resumed.Approved);
    }

    [Fact]
    public async Task Mismatched_request_is_rejected_without_consuming_the_pending_wait()
    {
        await using var host = await ReadinessWorkflowTestHost.CreateForOutcomesAsync(
            BranchOutcome.Passed);
        using var directory = new TemporaryDirectory();
        const string SessionId = "release-7-revision-1";
        PendingApprovalRequest started;

        using (var firstProcess = new CheckpointStoreCoordinator(directory.Info))
        {
            started = Assert.IsType<PendingApprovalRequest>(
                await firstProcess.StartAsync(
                    host.CreateWorkflow(),
                    host.Input,
                    SessionId));
        }

        using var secondProcess = new CheckpointStoreCoordinator(directory.Info);
        var mismatch = started with { RequestId = Guid.NewGuid().ToString("N") };

        var exception = await Assert.ThrowsAsync<WorkflowContinuationException>(
            () => secondProcess.ResumeApprovalAsync(
                host.CreateWorkflow(),
                SessionId,
                mismatch,
                new ApprovalResponse(Approved: true)));

        Assert.Equal(ContinuationFailureKind.Mismatched, exception.Kind);

        var resumed = await secondProcess.ResumeApprovalAsync(
            host.CreateWorkflow(),
            SessionId,
            started,
            new ApprovalResponse(Approved: true));
        Assert.True(resumed.Approved);
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
                new PendingApprovalRequest("missing-request"),
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
        PendingApprovalRequest started;

        using (var firstProcess = new CheckpointStoreCoordinator(directory.Info))
        {
            started = Assert.IsType<PendingApprovalRequest>(
                await firstProcess.StartAsync(
                    host.CreateWorkflow(),
                    host.Input,
                    SessionId));
        }

        var checkpointFile = Directory
            .EnumerateFiles(directory.Info.FullName, "*", SearchOption.AllDirectories)
            .Single(path => File.ReadAllText(path).Contains(started.RequestId, StringComparison.Ordinal));
        await File.WriteAllTextAsync(checkpointFile, "{not-valid-json");

        using var secondProcess = new CheckpointStoreCoordinator(directory.Info);
        var exception = await Assert.ThrowsAsync<WorkflowContinuationException>(
            () => secondProcess.ResumeApprovalAsync(
                host.CreateWorkflow(),
                SessionId,
                started,
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
        PendingApprovalRequest started;

        using (var firstProcess = new CheckpointStoreCoordinator(directory.Info))
        {
            started = Assert.IsType<PendingApprovalRequest>(
                await firstProcess.StartAsync(
                    host.CreateWorkflow(),
                    host.Input,
                    SessionId));
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
                started,
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
