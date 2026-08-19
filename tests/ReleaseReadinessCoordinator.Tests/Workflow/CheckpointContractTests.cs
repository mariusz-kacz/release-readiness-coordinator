using System.Text.Json;
using System.Text.Json.Nodes;
using ReleaseReadinessCoordinator.Data;
using ReleaseReadinessCoordinator.Tests.Readiness;
using ReleaseReadinessCoordinator.Workflow;
using ReleaseReadinessCoordinator.Domain;

namespace ReleaseReadinessCoordinator.Tests.Workflow;

public sealed class CheckpointContractTests
{
    [Fact]
    public async Task Restored_approval_wait_contains_the_checkpoint_carried_payload()
    {
        await using var host = await ReadinessWorkflowTestHost.CreateForOutcomesAsync(
            BranchOutcome.Passed);
        using var directory = new TemporaryDirectory();
        const string SessionId = "release-payload";
        PendingApprovalWait started;

        using (var firstProcess = new CheckpointStoreCoordinator(directory.Info))
        {
            started = Assert.IsType<PendingApprovalWait>(
                await firstProcess.StartAsync(
                    host.CreateWorkflow(),
                    host.Input,
                    SessionId));
        }

        var approval = Assert.IsType<ApprovalRequest>(started.Approval);
        var checkpointText = Directory
            .EnumerateFiles(directory.Info.FullName, "*", SearchOption.AllDirectories)
            .Select(File.ReadAllText)
            .Single(text => text.Contains(started.WorkflowRequestId, StringComparison.Ordinal));
        Assert.Contains(typeof(ApprovalRequest).FullName!, checkpointText, StringComparison.Ordinal);
        using var checkpoint = JsonDocument.Parse(checkpointText);
        var storedPayload = checkpoint.RootElement
            .GetProperty("runnerData")
            .GetProperty("outstandingRequests")[0]
            .GetProperty("data")
            .GetProperty("value");
        Assert.Equivalent(
            approval,
            storedPayload.Deserialize<ApprovalRequest>(
                new JsonSerializerOptions(JsonSerializerDefaults.Web)),
            strict: true);

        using var secondProcess = new CheckpointStoreCoordinator(directory.Info);
        var restored = Assert.IsType<PendingApprovalWait>(
            await secondProcess.RestoreAsync(host.CreateWorkflow(), SessionId));

        Assert.Equal(started.WorkflowRequestId, restored.WorkflowRequestId);
        Assert.Equivalent(
            approval,
            Assert.IsType<ApprovalRequest>(restored.Approval),
            strict: true);
    }

    [Theory]
    [InlineData("missing", (int)ContinuationFailureKind.Incompatible)]
    [InlineData("unreadable", (int)ContinuationFailureKind.Corrupt)]
    [InlineData("wrong-type", (int)ContinuationFailureKind.Mismatched)]
    public async Task Invalid_checkpoint_approval_payload_fails_closed(
        string corruption,
        int expectedKindValue)
    {
        await using var host = await ReadinessWorkflowTestHost.CreateForOutcomesAsync(
            BranchOutcome.Passed);
        using var directory = new TemporaryDirectory();
        var sessionId = $"release-payload-{corruption}";
        PendingApprovalWait started;

        using (var firstProcess = new CheckpointStoreCoordinator(directory.Info))
        {
            started = Assert.IsType<PendingApprovalWait>(
                await firstProcess.StartAsync(
                    host.CreateWorkflow(),
                    host.Input,
                    sessionId));
        }

        directory.CorruptApprovalPayload(started.WorkflowRequestId, corruption);
        using var secondProcess = new CheckpointStoreCoordinator(directory.Info);

        var exception = await Assert.ThrowsAsync<WorkflowContinuationException>(
            () => secondProcess.RestoreAsync(host.CreateWorkflow(), sessionId));

        Assert.Equal((ContinuationFailureKind)expectedKindValue, exception.Kind);
        var detail = await host.DataService.GetReleaseDetailAsync(host.Submission.ReleaseId);
        Assert.Null(detail!.TerminalResponse);
        Assert.DoesNotContain(
            detail.Timeline,
            entry => entry.Kind is TimelineEntryKind.HumanResponseAccepted);
    }

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

        public void CorruptApprovalPayload(string workflowRequestId, string corruption)
        {
            var checkpointPath = Directory
                .EnumerateFiles(Info.FullName, "*", SearchOption.AllDirectories)
                .Single(path => File.ReadAllText(path).Contains(workflowRequestId, StringComparison.Ordinal));
            var checkpoint = JsonNode.Parse(File.ReadAllText(checkpointPath))!.AsObject();
            var request = checkpoint["runnerData"]!["outstandingRequests"]!
                .AsArray()
                .Single()!
                .AsObject();
            var data = request["data"]!.AsObject();

            switch (corruption)
            {
                case "missing":
                    data.Remove("value");
                    break;
                case "unreadable":
                    data["value"]!["snapshot"]!["decisionBrief"] = "";
                    break;
                case "wrong-type":
                    data["typeId"]!["typeName"] = typeof(ApprovalResponse).FullName;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(corruption));
            }

            File.WriteAllText(checkpointPath, checkpoint.ToJsonString());
        }

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
