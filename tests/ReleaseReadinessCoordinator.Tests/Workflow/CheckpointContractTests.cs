using ReleaseReadinessCoordinator.Data;
using ReleaseReadinessCoordinator.Tests.Readiness;
using ReleaseReadinessCoordinator.Workflow;
using ReleaseReadinessCoordinator.Domain;

namespace ReleaseReadinessCoordinator.Tests.Workflow;

public sealed class CheckpointContractTests
{
    [Fact]
    public async Task Pending_wait_is_rehydrated_after_process_boundary_and_accepts_correlated_response()
    {
        await using var host = await ReadinessWorkflowTestHost.CreateForOutcomesAsync(
            BranchOutcome.Passed);
        using var directory = new TemporaryDirectory();
        const string SessionId = "release-42";
        PendingApprovalWait started;

        using (var firstProcess = new CheckpointStoreCoordinator(directory.Info))
        {
            started = Assert.IsType<PendingApprovalWait>(
                await firstProcess.StartAsync(
                    host.CreateWorkflow(),
                    host.Input,
                    SessionId));

            Assert.False(string.IsNullOrWhiteSpace(started.WorkflowRequestId));
        }

        var persistedJson = Directory
            .EnumerateFiles(directory.Info.FullName, "*", SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .ToArray();
        Assert.NotEmpty(persistedJson);
        Assert.Contains(persistedJson, json => json.Contains(started.WorkflowRequestId, StringComparison.Ordinal));

        using var secondProcess = new CheckpointStoreCoordinator(directory.Info);
        var resumed = await secondProcess.ResumeApprovalAsync(
            host.CreateWorkflow(),
            SessionId,
            started,
            Response(started));

        Assert.Equal(HumanDecision.Approve, resumed.Response.Decision);
    }

    [Fact]
    public async Task Mismatched_request_is_rejected_without_consuming_the_pending_wait()
    {
        await using var host = await ReadinessWorkflowTestHost.CreateForOutcomesAsync(
            BranchOutcome.Passed);
        using var directory = new TemporaryDirectory();
        const string SessionId = "release-7";
        PendingApprovalWait started;

        using (var firstProcess = new CheckpointStoreCoordinator(directory.Info))
        {
            started = Assert.IsType<PendingApprovalWait>(
                await firstProcess.StartAsync(
                    host.CreateWorkflow(),
                    host.Input,
                    SessionId));
        }

        using var secondProcess = new CheckpointStoreCoordinator(directory.Info);
        var mismatch = started with { WorkflowRequestId = Guid.NewGuid().ToString("N") };

        var exception = await Assert.ThrowsAsync<WorkflowContinuationException>(
            () => secondProcess.ResumeApprovalAsync(
                host.CreateWorkflow(),
                SessionId,
                mismatch,
                Response(started)));

        Assert.Equal(ContinuationFailureKind.Mismatched, exception.Kind);

        var resumed = await secondProcess.ResumeApprovalAsync(
            host.CreateWorkflow(),
            SessionId,
            started,
            Response(started));
        Assert.Equal(HumanDecision.Approve, resumed.Response.Decision);
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

    private static ApprovalResponse Response(PendingApprovalWait? pending)
    {
        var respondedAt = pending?.Approval?.Request.CreatedAt
            ?? new UtcInstant(DateTimeOffset.UtcNow);
        return new ApprovalResponse(new HumanResponse(
            Guid.NewGuid(),
            HumanDecision.Approve,
            "release-manager",
            "Reviewed.",
            respondedAt));
    }
}
