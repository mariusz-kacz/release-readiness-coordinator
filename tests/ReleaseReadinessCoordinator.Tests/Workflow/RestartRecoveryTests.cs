using Microsoft.EntityFrameworkCore;
using ReleaseReadinessCoordinator.Data;
using ReleaseReadinessCoordinator.Domain;
using ReleaseReadinessCoordinator.Workflow;

namespace ReleaseReadinessCoordinator.Tests.Workflow;

public sealed class RestartRecoveryTests
{
    [Fact]
    public async Task Incompatible_checkpoint_is_persisted_as_a_visible_failure()
    {
        await using var fixture = await RestartFixture.CreateAsync("restart-incompatible");
        PendingApprovalWait started;

        await using (var firstContext = fixture.CreateContext())
        using (var firstCoordinator = new CheckpointStoreCoordinator(fixture.CheckpointDirectory))
        {
            var dataService = new ApplicationDataService(firstContext);
            await fixture.SubmitAsync(dataService);
            var service = new ReleaseWorkflowService(dataService, firstCoordinator, fixture.Clock);
            started = Assert.IsType<PendingApprovalWait>(await service.StartAsync(
                fixture.Submission,
                new EvaluationRoundStart(Guid.NewGuid(), 1, fixture.Now, []),
                fixture.SessionId));
        }

        fixture.ReplaceCheckpointText(
            started.WorkflowRequestId,
            ReleaseWorkflowExecutorIds.Planner,
            "incompatible-planner");
        using var secondCoordinator = new CheckpointStoreCoordinator(fixture.CheckpointDirectory);
        await using var secondContext = fixture.CreateContext();
        var dataAfterRestart = new ApplicationDataService(secondContext);
        var serviceAfterRestart = new ReleaseWorkflowService(dataAfterRestart, secondCoordinator, fixture.Clock);

        var exception = await Assert.ThrowsAsync<WorkflowContinuationException>(
            () => serviceAfterRestart.RestoreAsync(fixture.Submission.ReleaseId));

        Assert.Equal(ContinuationFailureKind.Incompatible, exception.Kind);
        var detail = await dataAfterRestart.GetReleaseDetailAsync(fixture.Submission.ReleaseId);
        Assert.Equal(ProcessPhase.Failed, detail!.Release.Phase);
        Assert.Contains(
            "Incompatible",
            Assert.Single(detail.Timeline.Where(entry => entry.Kind is TimelineEntryKind.WorkflowFailed)).Summary,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Impossible_durable_state_is_persisted_as_a_visible_failure()
    {
        await using var fixture = await RestartFixture.CreateAsync("restart-impossible");

        await using (var firstContext = fixture.CreateContext())
        using (var firstCoordinator = new CheckpointStoreCoordinator(fixture.CheckpointDirectory))
        {
            var dataService = new ApplicationDataService(firstContext);
            await fixture.SubmitAsync(dataService);
            var service = new ReleaseWorkflowService(dataService, firstCoordinator, fixture.Clock);
            var started = Assert.IsType<PendingApprovalWait>(await service.StartAsync(
                fixture.Submission,
                new EvaluationRoundStart(Guid.NewGuid(), 1, fixture.Now, []),
                fixture.SessionId));
            await dataService.SaveWorkflowCorrelationAsync(
                new WorkflowCorrelationRecord(
                    fixture.Submission.ReleaseId,
                    fixture.SessionId,
                    started.WorkflowRequestId,
                    WorkflowRequestKind.Remediation,
                    fixture.Now),
                "restart:impossible-correlation");
        }

        using var secondCoordinator = new CheckpointStoreCoordinator(fixture.CheckpointDirectory);
        await using var secondContext = fixture.CreateContext();
        var dataAfterRestart = new ApplicationDataService(secondContext);
        var serviceAfterRestart = new ReleaseWorkflowService(dataAfterRestart, secondCoordinator, fixture.Clock);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => serviceAfterRestart.RestoreAsync(fixture.Submission.ReleaseId));

        var detail = await dataAfterRestart.GetReleaseDetailAsync(fixture.Submission.ReleaseId);
        Assert.Equal(ProcessPhase.Failed, detail!.Release.Phase);
        var failure = Assert.Single(
            detail.Timeline.Where(entry => entry.Kind is TimelineEntryKind.WorkflowFailed));
        Assert.Contains("ImpossibleState", failure.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SQLite_workflow_failure_is_persisted_after_failed_tracked_writes_are_discarded()
    {
        await using var fixture = await RestartFixture.CreateAsync("restart-sqlite");
        await using var context = fixture.CreateContext();
        var dataService = new ApplicationDataService(context);
        await fixture.SubmitAsync(dataService);
        await context.Database.ExecuteSqlRawAsync(
            """
            CREATE TRIGGER FailEvaluationRound
            BEFORE INSERT ON EvaluationRounds
            BEGIN
                SELECT RAISE(ABORT, 'simulated unrecoverable workflow write failure');
            END;
            """);
        using var coordinator = new CheckpointStoreCoordinator(fixture.CheckpointDirectory);
        var service = new ReleaseWorkflowService(dataService, coordinator, fixture.Clock);

        await Assert.ThrowsAnyAsync<Exception>(() => service.StartAsync(
            fixture.Submission,
            new EvaluationRoundStart(Guid.NewGuid(), 1, fixture.Now, []),
            fixture.SessionId));

        await using var verificationContext = fixture.CreateContext();
        var detail = await new ApplicationDataService(verificationContext)
            .GetReleaseDetailAsync(fixture.Submission.ReleaseId);
        Assert.NotNull(detail);
        Assert.Equal(ProcessPhase.Failed, detail.Release.Phase);
        var failure = Assert.Single(
            detail.Timeline.Where(entry => entry.Kind is TimelineEntryKind.WorkflowFailed));
        Assert.Contains("SQLite", failure.Summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, (int)ContinuationFailureKind.Missing)]
    [InlineData(true, (int)ContinuationFailureKind.Corrupt)]
    public async Task Invalid_continuation_is_persisted_as_a_visible_failure(
        bool corruptCheckpoint,
        int expectedKindValue)
    {
        var expectedKind = (ContinuationFailureKind)expectedKindValue;
        await using var fixture = await RestartFixture.CreateAsync($"restart-{expectedKind}");
        PendingApprovalWait started;

        await using (var firstContext = fixture.CreateContext())
        using (var firstCoordinator = new CheckpointStoreCoordinator(fixture.CheckpointDirectory))
        {
            var dataService = new ApplicationDataService(firstContext);
            await fixture.SubmitAsync(dataService);
            var service = new ReleaseWorkflowService(dataService, firstCoordinator, fixture.Clock);
            started = Assert.IsType<PendingApprovalWait>(await service.StartAsync(
                fixture.Submission,
                new EvaluationRoundStart(Guid.NewGuid(), 1, fixture.Now, []),
                fixture.SessionId));
        }

        if (corruptCheckpoint)
        {
            fixture.CorruptCheckpointContaining(started.WorkflowRequestId);
        }
        else
        {
            fixture.RemoveCheckpoints();
        }

        using var secondCoordinator = new CheckpointStoreCoordinator(fixture.CheckpointDirectory);
        await using var secondContext = fixture.CreateContext();
        var dataAfterRestart = new ApplicationDataService(secondContext);
        var serviceAfterRestart = new ReleaseWorkflowService(dataAfterRestart, secondCoordinator, fixture.Clock);

        var exception = await Assert.ThrowsAsync<WorkflowContinuationException>(
            () => serviceAfterRestart.RestoreAsync(fixture.Submission.ReleaseId));

        Assert.Equal(expectedKind, exception.Kind);
        var detail = await dataAfterRestart.GetReleaseDetailAsync(fixture.Submission.ReleaseId);
        Assert.NotNull(detail);
        Assert.Equal(ProcessPhase.Failed, detail.Release.Phase);
        var failure = Assert.Single(
            detail.Timeline.Where(entry => entry.Kind is TimelineEntryKind.WorkflowFailed));
        Assert.Contains(expectedKind.ToString(), failure.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Remediation_wait_survives_restart_and_reconciles_the_next_wait()
    {
        await using var fixture = await RestartFixture.CreateAsync("restart-remediation", blockedTest: true);
        PendingRemediationWait started;

        await using (var firstContext = fixture.CreateContext())
        using (var firstCoordinator = new CheckpointStoreCoordinator(fixture.CheckpointDirectory))
        {
            var dataService = new ApplicationDataService(firstContext);
            await fixture.SubmitAsync(dataService);
            var service = new ReleaseWorkflowService(dataService, firstCoordinator, fixture.Clock);
            started = Assert.IsType<PendingRemediationWait>(await service.StartAsync(
                fixture.Submission,
                new EvaluationRoundStart(Guid.NewGuid(), 1, fixture.Now, []),
                fixture.SessionId));
        }

        using var secondCoordinator = new CheckpointStoreCoordinator(fixture.CheckpointDirectory);
        await using var responseContext = fixture.CreateContext();
        await using var replayContext = fixture.CreateContext();
        var dataAfterRestart = new ApplicationDataService(responseContext);
        var serviceAfterRestart = new ReleaseWorkflowService(dataAfterRestart, secondCoordinator, fixture.Clock);
        var replayService = new ReleaseWorkflowService(
            new ApplicationDataService(replayContext),
            secondCoordinator,
            fixture.Clock);
        var restored = Assert.IsType<PendingRemediationWait>(
            await serviceAfterRestart.RestoreAsync(fixture.Submission.ReleaseId));
        Assert.Equal(started, restored);

        var detailBeforeResponse = await dataAfterRestart.GetReleaseDetailAsync(fixture.Submission.ReleaseId);
        var currentTestEvidence = Assert.IsType<TestEvidenceRecord>(
            detailBeforeResponse!.CurrentEvidence[EvidenceKind.Test]);
        var replacement = new TestEvidenceRecord(
            Guid.NewGuid(),
            fixture.Submission.ReleaseId,
            2,
            fixture.Now,
            currentTestEvidence.Id,
            fixture.Submission.ReleaseVersion,
            fixture.Now,
            0.99m,
            []);
        var submission = new RemediationSubmission(
            Guid.NewGuid(),
            started.RemediationRequestId!.Value,
            fixture.Now,
            new Dictionary<EvidenceKind, Guid> { [EvidenceKind.Test] = replacement.Id },
            []);

        var response = new RemediationWorkflowResponse(submission, [replacement]);
        var continuations = await Task.WhenAll(
            serviceAfterRestart.ResumeRemediationAsync(fixture.Submission.ReleaseId, response),
            replayService.ResumeRemediationAsync(fixture.Submission.ReleaseId, response));
        var nextWait = Assert.IsType<PendingApprovalWait>(continuations[0]);
        Assert.All(
            continuations,
            continuation => Assert.Equal(nextWait.WorkflowRequestId, continuation.WorkflowRequestId));

        var detail = await dataAfterRestart.GetReleaseDetailAsync(fixture.Submission.ReleaseId);
        Assert.NotNull(detail);
        Assert.Equal(ProcessPhase.WaitingForApproval, detail.Release.Phase);
        Assert.Equal(nextWait.WorkflowRequestId, detail.WorkflowCorrelation?.PendingWorkflowRequestId);
        Assert.Equal(WorkflowRequestKind.Approval, detail.WorkflowCorrelation?.PendingRequestKind);
        Assert.Single(detail.RemediationSubmissions);
        Assert.Equal(2, detail.EvaluationRounds.Length);
    }

    [Fact]
    public async Task Approval_wait_survives_restart_and_concurrent_replay_continues_once()
    {
        await using var fixture = await RestartFixture.CreateAsync("restart-approval");
        PendingApprovalWait started;

        await using (var firstContext = fixture.CreateContext())
        using (var firstCoordinator = new CheckpointStoreCoordinator(fixture.CheckpointDirectory))
        {
            var dataService = new ApplicationDataService(firstContext);
            await fixture.SubmitAsync(dataService);
            var service = new ReleaseWorkflowService(dataService, firstCoordinator, fixture.Clock);

            started = Assert.IsType<PendingApprovalWait>(await service.StartAsync(
                fixture.Submission,
                new EvaluationRoundStart(Guid.NewGuid(), 1, fixture.Now, []),
                fixture.SessionId));
        }

        using var secondCoordinator = new CheckpointStoreCoordinator(fixture.CheckpointDirectory);
        await using var firstResponseContext = fixture.CreateContext();
        await using var secondResponseContext = fixture.CreateContext();
        var firstService = new ReleaseWorkflowService(
            new ApplicationDataService(firstResponseContext),
            secondCoordinator,
            fixture.Clock);
        var secondService = new ReleaseWorkflowService(
            new ApplicationDataService(secondResponseContext),
            secondCoordinator,
            fixture.Clock);

        var restored = Assert.IsType<PendingApprovalWait>(
            await firstService.RestoreAsync(fixture.Submission.ReleaseId));
        Assert.Equal(started.WorkflowRequestId, restored.WorkflowRequestId);
        var approval = Assert.IsType<ApprovalRequest>(restored.Approval);
        var response = new ApprovalResponse(new HumanResponse(
            Guid.NewGuid(),
            HumanDecision.Approve,
            "release-manager",
            "Approved after restart.",
            fixture.Now));

        var continuations = await Task.WhenAll(
            firstService.ResumeApprovalAsync(fixture.Submission.ReleaseId, response),
            secondService.ResumeApprovalAsync(fixture.Submission.ReleaseId, response));

        Assert.All(continuations, continuation => Assert.Equal(response.Response.Id, continuation.Response.Id));
        Assert.All(continuations, continuation => Assert.Equal(approval.Request.Id, continuation.ApprovalRequestId));

        await using var verificationContext = fixture.CreateContext();
        var detail = await new ApplicationDataService(verificationContext)
            .GetReleaseDetailAsync(fixture.Submission.ReleaseId);
        Assert.NotNull(detail);
        Assert.Equal(ProcessPhase.Approved, detail.Release.Phase);
        Assert.Equal(response.Response.Id, detail.TerminalResponse?.Response.Id);
        Assert.Single(detail.Timeline.Where(entry => entry.Kind is TimelineEntryKind.HumanResponseAccepted));
        Assert.Single(detail.EvaluationRounds);
        Assert.Single(detail.HumanDecisionRequests);
    }

    private sealed class RestartFixture : IAsyncDisposable
    {
        private readonly string _rootPath;
        private readonly string _databasePath;

        private RestartFixture(string rootPath, string releaseId, bool blockedTest)
        {
            _rootPath = rootPath;
            _databasePath = Path.Combine(rootPath, "release-readiness.db");
            CheckpointDirectory = Directory.CreateDirectory(Path.Combine(rootPath, "workflow-checkpoints"));
            Now = Utc(2026, 8, 18, 10);
            Clock = new FixedTimeProvider(Now.Value);
            Submission = new ReleaseSubmission(
                new ReleaseId(releaseId),
                "orders",
                "2.4.0",
                new UtcInterval(Now, Utc(2026, 8, 18, 11)),
                Utc(2026, 8, 18, 9));
            SessionId = $"release-{releaseId}";
            BlockedTest = blockedTest;
        }

        public DirectoryInfo CheckpointDirectory { get; }

        public FixedTimeProvider Clock { get; }

        public UtcInstant Now { get; }

        public ReleaseSubmission Submission { get; }

        public string SessionId { get; }

        private bool BlockedTest { get; }

        public static async Task<RestartFixture> CreateAsync(
            string releaseId,
            bool blockedTest = false)
        {
            var rootPath = Path.Combine(
                Path.GetTempPath(),
                "release-readiness-restart-tests",
                Guid.NewGuid().ToString("N"));
            var fixture = new RestartFixture(rootPath, releaseId, blockedTest);
            await using var context = fixture.CreateContext();
            await context.Database.EnsureCreatedAsync();
            return fixture;
        }

        public AppDbContext CreateContext()
        {
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={_databasePath};Pooling=False;Default Timeout=30")
                .Options;
            return new AppDbContext(options);
        }

        public Task<Release> SubmitAsync(IApplicationDataService dataService) =>
            dataService.SubmitReleaseAsync(
                Submission,
                Evidence(),
                new TimelineEntry(
                    Guid.NewGuid(),
                    Submission.ReleaseId,
                    1,
                    TimelineEntryKind.ReleaseSubmitted,
                    "Release submitted.",
                    Submission.SubmittedAt),
                $"restart:{Submission.ReleaseId.Value}:submit");

        public void CorruptCheckpointContaining(string workflowRequestId)
        {
            var checkpointPath = Directory
                .EnumerateFiles(CheckpointDirectory.FullName, "*", SearchOption.AllDirectories)
                .Single(path => File.ReadAllText(path).Contains(workflowRequestId, StringComparison.Ordinal));
            File.WriteAllText(checkpointPath, "{not-valid-json");
        }

        public void RemoveCheckpoints()
        {
            foreach (var path in Directory.EnumerateFiles(
                CheckpointDirectory.FullName,
                "*",
                SearchOption.AllDirectories))
            {
                File.Delete(path);
            }
        }

        public void ReplaceCheckpointText(
            string workflowRequestId,
            string oldValue,
            string newValue)
        {
            var checkpointPath = Directory
                .EnumerateFiles(CheckpointDirectory.FullName, "*", SearchOption.AllDirectories)
                .Single(path => File.ReadAllText(path).Contains(workflowRequestId, StringComparison.Ordinal));
            var checkpoint = File.ReadAllText(checkpointPath);
            Assert.Contains(oldValue, checkpoint, StringComparison.Ordinal);
            File.WriteAllText(
                checkpointPath,
                checkpoint.Replace(oldValue, newValue, StringComparison.Ordinal));
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(_rootPath))
            {
                Directory.Delete(_rootPath, recursive: true);
            }

            return ValueTask.CompletedTask;
        }

        private EvidenceRecord[] Evidence() =>
        [
            new TestEvidenceRecord(
                Guid.NewGuid(), Submission.ReleaseId, 1, Submission.SubmittedAt, null,
                Submission.ReleaseVersion, Submission.SubmittedAt, BlockedTest ? 0m : 0.98m, []),
            new SecurityEvidenceRecord(
                Guid.NewGuid(), Submission.ReleaseId, 1, Submission.SubmittedAt, null,
                Submission.ReleaseVersion, Submission.SubmittedAt, [], [],
                new Dictionary<string, (string Scope, UtcInstant ExpiresAt)>()),
            new ChangeEvidenceRecord(
                Guid.NewGuid(), Submission.ReleaseId, 1, Submission.SubmittedAt, null,
                true, Submission.RequestedDeploymentWindow),
        ];
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static UtcInstant Utc(int year, int month, int day, int hour) =>
        new(new DateTimeOffset(year, month, day, hour, 0, 0, TimeSpan.Zero));
}
