using Microsoft.EntityFrameworkCore;
using ReleaseReadinessCoordinator.Data;
using ReleaseReadinessCoordinator.Domain;
using ReleaseReadinessCoordinator.Readiness;
using ReleaseReadinessCoordinator.Workflow;
using BranchResult = ReleaseReadinessCoordinator.Domain.BranchResult;

namespace ReleaseReadinessCoordinator.Tests.Workflow;

public sealed class RemediationWorkflowAggregationTests
{
    [Fact]
    public async Task Complete_fan_in_persists_one_round_and_one_request_with_every_problem()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var submission = Submission("aggregate-problems");
        var evidence = Evidence(submission);

        await using var context = database.CreateContext();
        var dataService = new ApplicationDataService(context);
        await SubmitAsync(dataService, submission, evidence);
        var startedAt = Utc(2026, 8, 17, 10);
        var start = new EvaluationRoundStart(Guid.NewGuid(), 1, startedAt, []);
        var results = new[]
        {
            Result(start, ReadinessCheck.Test, BranchOutcome.Blocked, evidence[0]),
            Result(start, ReadinessCheck.Security, BranchOutcome.MissingEvidence, evidence[1]),
            Result(start, ReadinessCheck.Change, BranchOutcome.Passed, evidence[2]),
        };
        var aggregator = new RoundAggregator(
            dataService,
            new FixedTimeProvider(Utc(2026, 8, 17, 10, 5).Value));

        var first = await aggregator.CompleteAsync(start, results);
        var replay = await aggregator.CompleteAsync(start, results);

        Assert.Equal(first.Round.Id, replay.Round.Id);
        Assert.Equal(
            first.Round.Results.Select(result => result.Id),
            replay.Round.Results.Select(result => result.Id));
        Assert.NotNull(first.RemediationRequest);
        Assert.NotNull(replay.RemediationRequest);
        Assert.Equal(first.RemediationRequest.Id, replay.RemediationRequest.Id);
        Assert.Equal(
            [ReadinessCheck.Test, ReadinessCheck.Security],
            first.RemediationRequest.Problems.Select(result => result.Check));

        var detail = await dataService.GetReleaseDetailAsync(submission.Key);
        Assert.NotNull(detail);
        Assert.Single(detail.EvaluationRounds);
        Assert.Single(detail.RemediationRequests);
        Assert.Equal(3, detail.EvaluationRounds[0].Results.Length);
        Assert.All(
            detail.EvaluationRounds[0].Results,
            result => Assert.StartsWith("Executed because", result.PlanningDetail));
        Assert.Equal(
            [TimelineEntryKind.ReleaseSubmitted, TimelineEntryKind.EvaluationCompleted, TimelineEntryKind.RemediationRequested],
            detail.Timeline.Select(entry => entry.Kind));
    }

    private static async Task SubmitAsync(
        IApplicationDataService dataService,
        ReleaseSubmission submission,
        IReadOnlyCollection<EvidenceRecord> evidence) =>
        await dataService.SubmitReleaseAsync(
            submission,
            evidence,
            new TimelineEntry(
                Guid.NewGuid(),
                submission.Key,
                1,
                TimelineEntryKind.ReleaseSubmitted,
                "Release submitted.",
                submission.SubmittedAt),
            $"test:{submission.Key.ReleaseId}:submit");

    private static BranchResult Result(
        EvaluationRoundStart start,
        ReadinessCheck check,
        BranchOutcome outcome,
        EvidenceRecord evidence) => new(
        Guid.NewGuid(),
        evidence.ReleaseRevision,
        start.RoundNumber,
        check,
        outcome,
        ExecutionDisposition.Executed,
        PlanningReason.InitialEvaluation,
        "Executed because no prior result exists.",
        evidence.Id,
        evidence.Kind,
        outcome is BranchOutcome.Passed ? Utc(2026, 8, 17, 12) : null,
        ["Attempt 1 succeeded."],
        new Dictionary<string, string> { ["result"] = outcome.ToString() },
        reuseSourceResultId: null,
        reuseSourceRound: null);

    private static ReleaseSubmission Submission(string releaseId) => new(
        new ReleaseRevisionKey(releaseId, 1),
        "orders",
        "2.4.0",
        new UtcInterval(Utc(2026, 8, 17, 10), Utc(2026, 8, 17, 11)),
        Utc(2026, 8, 17, 9));

    private static EvidenceRecord[] Evidence(ReleaseSubmission submission) =>
    [
        new TestEvidenceRecord(
            Guid.NewGuid(), submission.Key, 1, submission.SubmittedAt, null,
            submission.ReleaseVersion, submission.SubmittedAt, 0.90m, []),
        new SecurityEvidenceRecord(
            Guid.NewGuid(), submission.Key, 1, submission.SubmittedAt, null,
            submission.ReleaseVersion, submission.SubmittedAt, [], [],
            new Dictionary<string, (string Scope, UtcInstant ExpiresAt)>()),
        new ChangeEvidenceRecord(
            Guid.NewGuid(), submission.Key, 1, submission.SubmittedAt, null,
            true, submission.RequestedDeploymentWindow),
    ];

    private static UtcInstant Utc(int year, int month, int day, int hour, int minute = 0) =>
        new(new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.Zero));

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}

public sealed class RemediationWorkflowResponseTests
{
    [Fact]
    public async Task Correlated_response_replaces_only_named_evidence_records_and_replays_once()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var submission = Submission();
        var evidence = Evidence(submission);

        await using var context = database.CreateContext();
        var dataService = new ApplicationDataService(context);
        await dataService.SubmitReleaseAsync(
            submission,
            evidence,
            Timeline(submission.Key, 1, TimelineEntryKind.ReleaseSubmitted, "Release submitted."),
            "response:submit");
        var round = Round(submission, evidence);
        await dataService.SaveEvaluationRoundAsync(
            round,
            Timeline(submission.Key, 2, TimelineEntryKind.EvaluationCompleted, "Round completed."),
            "response:round:1");
        var request = new ReleaseReadinessCoordinator.Domain.RemediationRequest(
            Guid.NewGuid(),
            submission.Key,
            1,
            Utc(2026, 8, 17, 10),
            round.Results.Where(result => result.Outcome is not BranchOutcome.Passed));
        await dataService.OpenRemediationRequestAsync(
            request,
            Timeline(submission.Key, 3, TimelineEntryKind.RemediationRequested, "Remediation requested."),
            "response:request:1");

        var replacement = new TestEvidenceRecord(
            Guid.NewGuid(),
            submission.Key,
            2,
            Utc(2026, 8, 17, 10, 30),
            evidence[0].Id,
            submission.ReleaseVersion,
            Utc(2026, 8, 17, 10),
            0.99m,
            []);
        var remediation = new RemediationSubmission(
            Guid.NewGuid(),
            request.Id,
            Utc(2026, 8, 17, 10, 30),
            new Dictionary<EvidenceKind, Guid> { [EvidenceKind.Test] = replacement.Id },
            [ReadinessCheck.Change]);
        var response = new RemediationWorkflowResponse(remediation, [replacement]);
        var handler = new RemediationHandler(submission.Key, dataService);

        var mismatch = new RemediationSubmission(
            Guid.NewGuid(),
            Guid.NewGuid(),
            remediation.SubmittedAt,
            new Dictionary<EvidenceKind, Guid>(),
            []);
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => handler.HandleAsync(new RemediationWorkflowResponse(mismatch, [])));

        var first = await handler.HandleAsync(response);
        var replay = await handler.HandleAsync(response);

        Assert.Equal(2, first.RoundNumber);
        Assert.Equal(first.RoundNumber, replay.RoundNumber);
        Assert.Equal(new[] { ReadinessCheck.Change }, first.ExplicitlySelectedChecks.AsEnumerable());

        var detail = await dataService.GetReleaseDetailAsync(submission.Key);
        Assert.NotNull(detail);
        Assert.Equal(submission, detail.Release.Submission);
        Assert.Single(detail.RemediationSubmissions);
        Assert.Equal(
            new[] { ReadinessCheck.Change },
            detail.RemediationSubmissions[0].ExplicitlySelectedChecks.AsEnumerable());
        Assert.Equal(replacement.Id, detail.CurrentEvidence[EvidenceKind.Test].Id);
        Assert.Equal(evidence[1].Id, detail.CurrentEvidence[EvidenceKind.Security].Id);
        Assert.Equal(evidence[2].Id, detail.CurrentEvidence[EvidenceKind.Change].Id);
        Assert.Equal(2, detail.EvidenceHistory.Count(item => item.Kind is EvidenceKind.Test));
        Assert.Equal(ProcessPhase.Evaluating, detail.Release.Phase);
        Assert.Equal(4, detail.Timeline.Length);
    }

    private static EvaluationRound Round(
        ReleaseSubmission submission,
        IReadOnlyList<EvidenceRecord> evidence)
    {
        var start = new EvaluationRoundStart(Guid.NewGuid(), 1, Utc(2026, 8, 17, 9, 30), []);
        return new EvaluationRound(
            start.Id,
            submission.Key,
            1,
            start.StartedAt,
            Utc(2026, 8, 17, 10),
            [
                Result(start, ReadinessCheck.Test, BranchOutcome.Blocked, evidence[0]),
                Result(start, ReadinessCheck.Security, BranchOutcome.Passed, evidence[1]),
                Result(start, ReadinessCheck.Change, BranchOutcome.Passed, evidence[2]),
            ]);
    }

    private static BranchResult Result(
        EvaluationRoundStart start,
        ReadinessCheck check,
        BranchOutcome outcome,
        EvidenceRecord evidence) => new(
        Guid.NewGuid(),
        evidence.ReleaseRevision,
        start.RoundNumber,
        check,
        outcome,
        ExecutionDisposition.Executed,
        PlanningReason.InitialEvaluation,
        "Executed because no prior result exists.",
        evidence.Id,
        evidence.Kind,
        outcome is BranchOutcome.Passed ? Utc(2026, 8, 17, 12) : null,
        ["Attempt 1 succeeded."],
        new Dictionary<string, string> { ["result"] = outcome.ToString() },
        null,
        null);

    private static TimelineEntry Timeline(
        ReleaseRevisionKey key,
        long sequence,
        TimelineEntryKind kind,
        string summary) => new(Guid.NewGuid(), key, sequence, kind, summary, Utc(2026, 8, 17, 10));

    private static ReleaseSubmission Submission() => new(
        new ReleaseRevisionKey("correlated-response", 1),
        "orders",
        "2.4.0",
        new UtcInterval(Utc(2026, 8, 17, 10), Utc(2026, 8, 17, 11)),
        Utc(2026, 8, 17, 9));

    private static EvidenceRecord[] Evidence(ReleaseSubmission submission) =>
    [
        new TestEvidenceRecord(
            Guid.NewGuid(), submission.Key, 1, submission.SubmittedAt, null,
            submission.ReleaseVersion, submission.SubmittedAt, 0.90m, []),
        new SecurityEvidenceRecord(
            Guid.NewGuid(), submission.Key, 1, submission.SubmittedAt, null,
            submission.ReleaseVersion, submission.SubmittedAt, [], [],
            new Dictionary<string, (string Scope, UtcInstant ExpiresAt)>()),
        new ChangeEvidenceRecord(
            Guid.NewGuid(), submission.Key, 1, submission.SubmittedAt, null,
            true, submission.RequestedDeploymentWindow),
    ];

    private static UtcInstant Utc(int year, int month, int day, int hour, int minute = 0) =>
        new(new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.Zero));
}

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
                Guid.NewGuid(), submission.Key, 1, TimelineEntryKind.ReleaseSubmitted,
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
        var started = await coordinator.StartAsync(
            Workflow(),
            new EvaluationRoundStart(Guid.NewGuid(), 1, now, []),
            "real-graph-remediation");

        Assert.Equal(ExternalWaitKind.Remediation, started.PendingRequest.Kind);
        var afterFirstRound = await dataService.GetReleaseDetailAsync(submission.Key);
        Assert.NotNull(afterFirstRound);
        var request = Assert.Single(afterFirstRound.RemediationRequests);
        Assert.Equal(
            [ReadinessCheck.Test, ReadinessCheck.Security],
            request.Problems.Select(result => result.Check));

        var replacements = ReplacementEvidence(submission, initialEvidence);
        var remediation = new RemediationSubmission(
            Guid.NewGuid(),
            request.Id,
            Utc(2026, 8, 17, 10, 15),
            replacements.ToDictionary(item => item.Kind, item => item.Id),
            []);
        var response = new RemediationWorkflowResponse(remediation, replacements);
        var mismatched = new RemediationWorkflowResponse(
            new RemediationSubmission(
                Guid.NewGuid(),
                Guid.NewGuid(),
                remediation.SubmittedAt,
                new Dictionary<EvidenceKind, Guid>(),
                []),
            []);

        await Assert.ThrowsAsync<WorkflowContinuationException>(() =>
            coordinator.ResumeRemediationAsync(
                Workflow(),
                "real-graph-remediation",
                started.PendingRequest,
                mismatched));
        Assert.Empty((await dataService.GetReleaseDetailAsync(submission.Key))!.RemediationSubmissions);

        timeProvider.SetUtcNow(Utc(2026, 8, 17, 10, 20).Value);
        var resumed = await coordinator.ResumeRemediationAsync(
            Workflow(),
            "real-graph-remediation",
            started.PendingRequest,
            response);

        Assert.Equal(ExternalWaitKind.Approval, resumed.NextRequest.Kind);
        var detail = await dataService.GetReleaseDetailAsync(submission.Key);
        Assert.NotNull(detail);
        Assert.Equal(2, detail.EvaluationRounds.Length);
        var secondRound = detail.EvaluationRounds[1];
        Assert.Collection(
            secondRound.Results,
            test =>
            {
                Assert.Equal(ExecutionDisposition.Executed, test.Disposition);
                Assert.Equal(PlanningReason.EvidenceChanged, test.PlanningReason);
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
                started.PendingRequest,
                response));
        detail = await dataService.GetReleaseDetailAsync(submission.Key);
        Assert.NotNull(detail);
        Assert.Equal(2, detail.EvaluationRounds.Length);
        Assert.Single(detail.RemediationSubmissions);
    }

    private static ReleaseSubmission Submission() => new(
        new ReleaseRevisionKey("real-graph-remediation", 1),
        "orders",
        "2.4.0",
        new UtcInterval(Utc(2026, 8, 17, 10), Utc(2026, 8, 17, 11)),
        Utc(2026, 8, 17, 9));

    private static EvidenceRecord[] InitialEvidence(ReleaseSubmission submission) =>
    [
        new TestEvidenceRecord(
            Guid.NewGuid(), submission.Key, 1, submission.SubmittedAt, null,
            submission.ReleaseVersion, submission.SubmittedAt, 0.90m, []),
        new SecurityEvidenceRecord(
            Guid.NewGuid(), submission.Key, 1, submission.SubmittedAt, null,
            submission.ReleaseVersion, submission.SubmittedAt, ["CRITICAL-1"], [],
            new Dictionary<string, (string Scope, UtcInstant ExpiresAt)>()),
        new ChangeEvidenceRecord(
            Guid.NewGuid(), submission.Key, 1, submission.SubmittedAt, null,
            true, new UtcInterval(Utc(2026, 8, 17, 9), Utc(2026, 8, 17, 12))),
    ];

    private static EvidenceRecord[] ReplacementEvidence(
        ReleaseSubmission submission,
        IReadOnlyList<EvidenceRecord> initialEvidence) =>
    [
        new TestEvidenceRecord(
            Guid.NewGuid(), submission.Key, 2, Utc(2026, 8, 17, 10, 15), initialEvidence[0].Id,
            submission.ReleaseVersion, Utc(2026, 8, 17, 10), 0.99m, []),
        new SecurityEvidenceRecord(
            Guid.NewGuid(), submission.Key, 2, Utc(2026, 8, 17, 10, 15), initialEvidence[1].Id,
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
            ReleaseRevisionKey releaseRevision,
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
            ReleaseRevisionKey releaseRevision,
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
            ReleaseRevisionKey releaseRevision,
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
