using Microsoft.EntityFrameworkCore;
using ReleaseReadinessCoordinator.Data;
using ReleaseReadinessCoordinator.Domain;
using ReleaseReadinessCoordinator.Readiness;
using ReleaseReadinessCoordinator.Workflow;

namespace ReleaseReadinessCoordinator.Tests.Workflow;

public sealed class RemediationWorkflowRealGraphTests
{
    [Fact]
    public async Task Real_graph_remediates_multiple_problems_and_reuses_the_unaffected_branch()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        using var checkpoints = new TemporaryCheckpointDirectory();
        var submission = Submission();
        var initialEvidence = InitialEvidence(submission);
        var now = Utc(2026, 8, 17, 10);
        var timeProvider = new FixedTimeProvider(now.Value);

        await using var context = database.CreateContext();
        IApplicationDataService dataService = new ApplicationDataService(context);
        await dataService.SubmitReleaseAsync(
            submission,
            initialEvidence,
            new TimelineEntry(
                Guid.NewGuid(), submission.ReleaseId, 1, TimelineEntryKind.ReleaseSubmitted,
                "Release submitted.", submission.SubmittedAt),
            "real-graph:submit");
        var testProvider = new CountingTestProvider(dataService);
        var securityProvider = new CountingSecurityProvider(dataService);
        var changeProvider = new CountingChangeProvider(dataService);
        var testPolicy = new CountingTestPolicy(new TestReadinessPolicy(timeProvider));
        var securityPolicy = new CountingSecurityPolicy(new SecurityReadinessPolicy(timeProvider));
        var changePolicy = new CountingChangePolicy(new ChangeReadinessPolicy());
        var dependencies = new ReadinessWorkflowDependencies(
            testProvider,
            testPolicy,
            securityProvider,
            securityPolicy,
            changeProvider,
            changePolicy);
        Microsoft.Agents.AI.Workflows.Workflow Workflow() =>
            ReleaseWorkflowFactory.Create(submission, dataService, timeProvider, dependencies);

        using var coordinator = new CheckpointStoreCoordinator(checkpoints.Info);
        var start = new EvaluationRoundStart(Guid.NewGuid(), 1, now, []);
        var started = Assert.IsType<PendingRemediationWait>(
            await coordinator.StartAsync(
                Workflow(),
                start,
                "real-graph-remediation"));

        var afterFirstRound = await dataService.GetReleaseDetailAsync(submission.ReleaseId);
        Assert.NotNull(afterFirstRound);
        var request = Assert.Single(afterFirstRound.RemediationRequests);
        Assert.Equal(
            [ReadinessCheck.Test, ReadinessCheck.Security],
            request.Problems.Select(result => result.Check));
        var replay = await new RoundAggregator(dataService, timeProvider)
            .CompleteAsync(start, afterFirstRound.EvaluationRounds[0].Results);
        Assert.Equal(request.Id, replay.RemediationRequest?.Id);

        var replacements = ReplacementEvidence(submission, initialEvidence);
        var remediation = new RemediationSubmission(
            Guid.NewGuid(),
            request.Id,
            Utc(2026, 8, 17, 10, 15),
            replacements.ToDictionary(item => item.Kind, item => item.Id),
            [ReadinessCheck.Test]);
        var response = new RemediationWorkflowResponse(remediation, replacements);
        var mismatched = new RemediationWorkflowResponse(
            new RemediationSubmission(
                Guid.NewGuid(),
                Guid.NewGuid(),
                remediation.SubmittedAt,
                new Dictionary<EvidenceKind, Guid>(),
                []),
            []);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            new RemediationHandler(submission.ReleaseId, dataService).HandleAsync(mismatched));

        await Assert.ThrowsAsync<WorkflowContinuationException>(() =>
            coordinator.ResumeRemediationAsync(
                Workflow(),
                "real-graph-remediation",
                started,
                mismatched));
        Assert.Empty((await dataService.GetReleaseDetailAsync(submission.ReleaseId))!.RemediationSubmissions);

        timeProvider.SetUtcNow(Utc(2026, 8, 17, 10, 20).Value);
        var resumed = await coordinator.ResumeRemediationAsync(
            Workflow(),
            "real-graph-remediation",
            started,
            response);

        Assert.IsType<PendingApprovalWait>(resumed);
        var detail = await dataService.GetReleaseDetailAsync(submission.ReleaseId);
        Assert.NotNull(detail);
        Assert.Equal(2, detail.EvaluationRounds.Length);
        var secondRound = detail.EvaluationRounds[1];
        Assert.Collection(
            secondRound.Results,
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
                Assert.Equal(detail.EvaluationRounds[0].Results[2].Id, change.ReuseSourceResultId);
                Assert.Equal(1, change.ReuseSourceRound);
            });
        Assert.Equal(2, testProvider.CallCount);
        Assert.Equal(2, securityProvider.CallCount);
        Assert.Equal(1, changeProvider.CallCount);
        Assert.Equal(2, testPolicy.CallCount);
        Assert.Equal(2, securityPolicy.CallCount);
        Assert.Equal(1, changePolicy.CallCount);

        await Assert.ThrowsAsync<WorkflowContinuationException>(() =>
            coordinator.ResumeRemediationAsync(
                Workflow(),
                "real-graph-remediation",
                started,
                response));
        detail = await dataService.GetReleaseDetailAsync(submission.ReleaseId);
        Assert.NotNull(detail);
        Assert.Equal(2, detail.EvaluationRounds.Length);
        Assert.Single(detail.RemediationSubmissions);
    }

    private static ReleaseSubmission Submission() => new(
        new ReleaseId("real-graph-remediation"),
        "orders",
        "2.4.0",
        new UtcInterval(Utc(2026, 8, 17, 10), Utc(2026, 8, 17, 11)),
        Utc(2026, 8, 17, 9));

    private static EvidenceRecord[] InitialEvidence(ReleaseSubmission submission) =>
    [
        new TestEvidenceRecord(
            Guid.NewGuid(), submission.ReleaseId, 1, submission.SubmittedAt, null,
            submission.ReleaseVersion, submission.SubmittedAt, 0.90m, []),
        new SecurityEvidenceRecord(
            Guid.NewGuid(), submission.ReleaseId, 1, submission.SubmittedAt, null,
            submission.ReleaseVersion, submission.SubmittedAt, ["CRITICAL-1"], [],
            new Dictionary<string, (string Scope, UtcInstant ExpiresAt)>()),
        new ChangeEvidenceRecord(
            Guid.NewGuid(), submission.ReleaseId, 1, submission.SubmittedAt, null,
            true, new UtcInterval(Utc(2026, 8, 17, 9), Utc(2026, 8, 17, 12))),
    ];

    private static EvidenceRecord[] ReplacementEvidence(
        ReleaseSubmission submission,
        IReadOnlyList<EvidenceRecord> initialEvidence) =>
    [
        new TestEvidenceRecord(
            Guid.NewGuid(), submission.ReleaseId, 2, Utc(2026, 8, 17, 10, 15), initialEvidence[0].Id,
            submission.ReleaseVersion, Utc(2026, 8, 17, 10), 0.99m, []),
        new SecurityEvidenceRecord(
            Guid.NewGuid(), submission.ReleaseId, 2, Utc(2026, 8, 17, 10, 15), initialEvidence[1].Id,
            submission.ReleaseVersion, Utc(2026, 8, 17, 10), [], [],
            new Dictionary<string, (string Scope, UtcInstant ExpiresAt)>()),
    ];

    private static UtcInstant Utc(int year, int month, int day, int hour, int minute = 0) =>
        new(new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.Zero));

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void SetUtcNow(DateTimeOffset value) => _utcNow = value;
    }

    private sealed class CountingTestProvider(IApplicationDataService dataService) : ITestEvidenceProvider
    {
        public int CallCount { get; private set; }

        public async ValueTask<TestEvidenceRecord?> GetCurrentAsync(
            ReleaseId releaseRevision,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return (TestEvidenceRecord?)await dataService.GetCurrentEvidenceAsync(
                releaseRevision, EvidenceKind.Test, cancellationToken);
        }
    }

    private sealed class CountingSecurityProvider(IApplicationDataService dataService) : ISecurityEvidenceProvider
    {
        public int CallCount { get; private set; }

        public async ValueTask<SecurityEvidenceRecord?> GetCurrentAsync(
            ReleaseId releaseRevision,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return (SecurityEvidenceRecord?)await dataService.GetCurrentEvidenceAsync(
                releaseRevision, EvidenceKind.Security, cancellationToken);
        }
    }

    private sealed class CountingChangeProvider(IApplicationDataService dataService) : IChangeEvidenceProvider
    {
        public int CallCount { get; private set; }

        public async ValueTask<ChangeEvidenceRecord?> GetCurrentAsync(
            ReleaseId releaseRevision,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return (ChangeEvidenceRecord?)await dataService.GetCurrentEvidenceAsync(
                releaseRevision, EvidenceKind.Change, cancellationToken);
        }
    }

    private sealed class CountingTestPolicy(ITestReadinessPolicy inner) : ITestReadinessPolicy
    {
        public int CallCount { get; private set; }
        public TestPolicyEvaluation Evaluate(ReleaseSubmission submission, TestEvidenceRecord evidence)
        {
            CallCount++;
            return inner.Evaluate(submission, evidence);
        }
    }

    private sealed class CountingSecurityPolicy(ISecurityReadinessPolicy inner) : ISecurityReadinessPolicy
    {
        public int CallCount { get; private set; }
        public SecurityPolicyEvaluation Evaluate(ReleaseSubmission submission, SecurityEvidenceRecord evidence)
        {
            CallCount++;
            return inner.Evaluate(submission, evidence);
        }
    }

    private sealed class CountingChangePolicy(IChangeReadinessPolicy inner) : IChangeReadinessPolicy
    {
        public int CallCount { get; private set; }
        public ChangePolicyEvaluation Evaluate(ReleaseSubmission submission, ChangeEvidenceRecord evidence)
        {
            CallCount++;
            return inner.Evaluate(submission, evidence);
        }
    }

    private sealed class TemporaryCheckpointDirectory : IDisposable
    {
        public TemporaryCheckpointDirectory()
        {
            Info = Directory.CreateDirectory(
                Path.Combine(Path.GetTempPath(), "release-readiness-checkpoints", Guid.NewGuid().ToString("N")));
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
internal sealed class TemporaryDatabase : IAsyncDisposable
{
    private TemporaryDatabase(string path)
    {
        Path = path;
    }

    private string Path { get; }

    public static async Task<TemporaryDatabase> CreateAsync()
    {
        var database = new TemporaryDatabase(
            System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"release-readiness-workflow-{Guid.NewGuid():N}.db"));
        await using var context = database.CreateContext();
        await context.Database.EnsureCreatedAsync();
        return database;
    }

    public AppDbContext CreateContext()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseSqlite($"Data Source={Path};Pooling=False;Default Timeout=30")
            .Options;
        return new AppDbContext(options);
    }

    public ValueTask DisposeAsync()
    {
        File.Delete(Path);
        return ValueTask.CompletedTask;
    }
}
