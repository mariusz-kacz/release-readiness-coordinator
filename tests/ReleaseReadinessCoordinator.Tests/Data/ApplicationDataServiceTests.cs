using Microsoft.EntityFrameworkCore;
using ReleaseReadinessCoordinator.Data;
using ReleaseReadinessCoordinator.Domain;

namespace ReleaseReadinessCoordinator.Tests.Data;

public sealed class ApplicationDataServiceTests
{
    [Fact]
    public async Task Submission_replay_after_reopening_the_database_has_one_durable_effect()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var submission = CreateSubmission("release-replay");
        var evidence = CreateTestEvidence(submission.Key, Guid.NewGuid(), 1, null, 0.98m);
        var timeline = CreateTimeline(submission.Key, 1, TimelineEntryKind.ReleaseSubmitted, "Release submitted.");

        await using (var firstContext = database.CreateContext())
        {
            var firstService = new ApplicationDataService(firstContext);
            var result = await firstService.SubmitReleaseAsync(
                submission,
                [evidence],
                timeline,
                "submit:release-replay:1");
            Assert.Equal(submission, result.Submission);
        }

        await using (var replayContext = database.CreateContext())
        {
            var replayService = new ApplicationDataService(replayContext);
            var replayed = await replayService.SubmitReleaseAsync(
                submission,
                [evidence],
                timeline,
                "submit:release-replay:1");
            Assert.Equal(submission, replayed.Submission);
        }

        await using var verification = database.CreateContext();
        Assert.Equal(1, await verification.ReleaseRevisions.CountAsync());
        Assert.Equal(1, await verification.EvidenceRecords.CountAsync());
        Assert.Equal(1, await verification.CurrentEvidence.CountAsync());
        Assert.Equal(1, await verification.TimelineEntries.CountAsync());
    }

    [Fact]
    public async Task Duplicate_release_revision_with_a_different_operation_is_a_conflict()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var submission = CreateSubmission("release-duplicate");

        await using var context = database.CreateContext();
        var service = new ApplicationDataService(context);
        await service.SubmitReleaseAsync(
            submission,
            [],
            CreateTimeline(submission.Key, 1, TimelineEntryKind.ReleaseSubmitted, "Release submitted."),
            "submit:first");

        var conflict = await Assert.ThrowsAsync<ApplicationDataConflictException>(() =>
            service.SubmitReleaseAsync(
                submission,
                [],
                CreateTimeline(submission.Key, 2, TimelineEntryKind.ReleaseSubmitted, "Submitted again."),
                "submit:second"));

        Assert.Equal(ApplicationDataConflictKind.DuplicateReleaseRevision, conflict.Kind);
    }

    [Fact]
    public async Task Reusing_an_operation_key_for_different_submission_fails_loudly()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var submission = CreateSubmission("release-operation-payload");

        await using var context = database.CreateContext();
        var service = new ApplicationDataService(context);
        await service.SubmitReleaseAsync(
            submission,
            [],
            CreateTimeline(submission.Key, 1, TimelineEntryKind.ReleaseSubmitted, "Release submitted."),
            "submit:payload-guard");

        var changedSubmission = new ReleaseSubmission(
            submission.Key,
            "different-service",
            submission.ReleaseVersion,
            submission.RequestedDeploymentWindow,
            submission.SubmittedAt);

        var conflict = await Assert.ThrowsAsync<ApplicationDataConflictException>(() =>
            service.SubmitReleaseAsync(
                changedSubmission,
                [],
                CreateTimeline(submission.Key, 1, TimelineEntryKind.ReleaseSubmitted, "Release submitted."),
                "submit:payload-guard"));

        Assert.Equal(ApplicationDataConflictKind.OperationKeyReused, conflict.Kind);
    }

    [Fact]
    public async Task Evidence_replacement_is_atomic_and_projection_preserves_metadata_and_history()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var submission = CreateSubmission("release-evidence");
        var original = CreateTestEvidence(submission.Key, Guid.NewGuid(), 1, null, 0.96m);

        await using var context = database.CreateContext();
        var service = new ApplicationDataService(context);
        await service.SubmitReleaseAsync(
            submission,
            [original],
            CreateTimeline(submission.Key, 1, TimelineEntryKind.ReleaseSubmitted, "Release submitted."),
            "submit:evidence");

        var replacement = CreateTestEvidence(submission.Key, Guid.NewGuid(), 2, original.Id, 0.99m);
        var persisted = await service.ReplaceEvidenceAsync(
            replacement,
            CreateTimeline(submission.Key, 2, TimelineEntryKind.RemediationSubmitted, "Test evidence replaced."),
            "evidence:test:2");
        var replayed = await service.ReplaceEvidenceAsync(
            replacement,
            CreateTimeline(submission.Key, 2, TimelineEntryKind.RemediationSubmitted, "Test evidence replaced."),
            "evidence:test:2");

        Assert.Equal(replacement, persisted);
        Assert.Equal(replacement, replayed);

        var detail = await service.GetReleaseDetailAsync(submission.Key);
        Assert.NotNull(detail);
        Assert.Equal(submission.Key, detail.Release.Submission.Key);
        Assert.Equal(submission.ServiceName, detail.Release.Submission.ServiceName);
        Assert.Equal(submission.ReleaseVersion, detail.Release.Submission.ReleaseVersion);
        Assert.Equal(submission.RequestedDeploymentWindow, detail.Release.Submission.RequestedDeploymentWindow);
        Assert.Equal(submission.SubmittedAt, detail.Release.Submission.SubmittedAt);
        Assert.Equal(2, detail.EvidenceHistory.Length);
        var current = Assert.IsType<TestEvidenceRecord>(detail.CurrentEvidence[EvidenceKind.Test]);
        Assert.Equal(replacement.Id, current.Id);
        Assert.Equal(replacement.Version, current.Version);
        Assert.Equal(replacement.SupersedesEvidenceId, current.SupersedesEvidenceId);
        Assert.Equal(replacement.PassRate, current.PassRate);
        Assert.Equal([1, 2], detail.EvidenceHistory.Select(item => item.Version));
        Assert.Equal(2, detail.Timeline.Length);

        await using var verification = database.CreateContext();
        Assert.Equal(2, await verification.EvidenceRecords.CountAsync());
        Assert.Equal(1, await verification.CurrentEvidence.CountAsync());
        Assert.Equal(replacement.Id, (await verification.CurrentEvidence.SingleAsync()).EvidenceId);
        Assert.Equal(2, await verification.TimelineEntries.CountAsync());
    }

    [Fact]
    public async Task Terminal_release_rejects_new_evidence_without_partial_history()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var submission = CreateSubmission("release-terminal");
        var original = CreateTestEvidence(submission.Key, Guid.NewGuid(), 1, null, 0.96m);

        await using (var setup = database.CreateContext())
        {
            var service = new ApplicationDataService(setup);
            await service.SubmitReleaseAsync(
                submission,
                [original],
                CreateTimeline(submission.Key, 1, TimelineEntryKind.ReleaseSubmitted, "Release submitted."),
                "submit:terminal");
            var release = await setup.ReleaseRevisions.SingleAsync();
            release.Phase = ProcessPhase.Approved;
            release.PhaseChangedAtUtc = new DateTimeOffset(2026, 8, 16, 10, 0, 0, TimeSpan.Zero);
            release.ConcurrencyToken = "release-approved";
            await setup.SaveChangesAsync();
        }

        await using var context = database.CreateContext();
        var dataService = new ApplicationDataService(context);
        var replacement = CreateTestEvidence(submission.Key, Guid.NewGuid(), 2, original.Id, 0.99m);

        var conflict = await Assert.ThrowsAsync<ApplicationDataConflictException>(() =>
            dataService.ReplaceEvidenceAsync(
                replacement,
                CreateTimeline(submission.Key, 2, TimelineEntryKind.RemediationSubmitted, "Should not persist."),
                "evidence:terminal"));

        Assert.Equal(ApplicationDataConflictKind.TerminalRelease, conflict.Kind);
        Assert.Equal(1, await context.EvidenceRecords.CountAsync());
        Assert.Equal(1, await context.TimelineEntries.CountAsync());
    }

    [Fact]
    public async Task Workflow_history_operations_replay_without_duplicate_rows_and_read_as_one_projection()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var submission = CreateSubmission("release-history");
        var testEvidence = CreateTestEvidence(submission.Key, Guid.NewGuid(), 1, null, 0.75m);
        var changeEvidence = CreateChangeEvidence(submission.Key, Guid.NewGuid());

        await using var context = database.CreateContext();
        var service = new ApplicationDataService(context);
        await service.SubmitReleaseAsync(
            submission,
            [testEvidence, changeEvidence],
            CreateTimeline(submission.Key, 1, TimelineEntryKind.ReleaseSubmitted, "Release submitted."),
            "history:submit");

        var round = CreateRound(submission.Key, 1, testEvidence, changeEvidence, passed: false);
        await service.SaveEvaluationRoundAsync(
            round,
            CreateTimeline(submission.Key, 2, TimelineEntryKind.EvaluationCompleted, "Evaluation completed."),
            "history:round:1");
        await service.SaveEvaluationRoundAsync(
            round,
            CreateTimeline(submission.Key, 2, TimelineEntryKind.EvaluationCompleted, "Evaluation completed."),
            "history:round:1");

        var request = new RemediationRequest(
            Guid.NewGuid(), submission.Key, round.RoundNumber, Utc(2026, 8, 16, 10), round.Results);
        await service.OpenRemediationRequestAsync(
            request,
            CreateTimeline(submission.Key, 3, TimelineEntryKind.RemediationRequested, "Remediation requested."),
            "history:request");

        var replacement = CreateTestEvidence(submission.Key, Guid.NewGuid(), 2, testEvidence.Id, 0.99m);
        var remediation = new RemediationSubmission(
            Guid.NewGuid(),
            request.Id,
            Utc(2026, 8, 16, 11),
            new Dictionary<EvidenceKind, Guid> { [EvidenceKind.Test] = replacement.Id },
            [ReadinessCheck.Test]);
        await service.SaveRemediationSubmissionAsync(
            submission.Key,
            remediation,
            [replacement],
            CreateTimeline(submission.Key, 4, TimelineEntryKind.RemediationSubmitted, "Remediation submitted."),
            "history:remediation");
        await service.SaveRemediationSubmissionAsync(
            submission.Key,
            remediation,
            [replacement],
            CreateTimeline(submission.Key, 4, TimelineEntryKind.RemediationSubmitted, "Remediation submitted."),
            "history:remediation");

        var correlation = new WorkflowCorrelationRecord(
            submission.Key, "session-history", "request-history", WorkflowRequestKind.Remediation, Utc(2026, 8, 16, 11));
        await service.SaveWorkflowCorrelationAsync(correlation, "history:correlation");
        await service.SaveWorkflowCorrelationAsync(correlation, "history:correlation");

        var extraTimeline = CreateTimeline(submission.Key, 5, TimelineEntryKind.EvaluationStarted, "Evaluation restarted.");
        await service.AppendTimelineEntryAsync(extraTimeline, "history:timeline:5");
        await service.AppendTimelineEntryAsync(extraTimeline, "history:timeline:5");

        var failureTimeline = CreateTimeline(submission.Key, 6, TimelineEntryKind.WorkflowFailed, "Workflow failed.");
        await service.MarkWorkflowFailedAsync(submission.Key, Utc(2026, 8, 16, 12), failureTimeline, "history:failure");
        await service.MarkWorkflowFailedAsync(submission.Key, Utc(2026, 8, 16, 12), failureTimeline, "history:failure");

        var detail = await service.GetReleaseDetailAsync(submission.Key);
        Assert.NotNull(detail);
        Assert.Equal(ProcessPhase.Failed, detail.Release.Phase);
        Assert.Single(detail.EvaluationRounds);
        Assert.Single(detail.RemediationRequests);
        Assert.Single(detail.RemediationSubmissions);
        Assert.Equal(replacement.Id, detail.CurrentEvidence[EvidenceKind.Test].Id);
        Assert.Equal(correlation.WorkflowSessionId, detail.WorkflowCorrelation?.WorkflowSessionId);
        Assert.Equal(6, detail.Timeline.Length);

        Assert.Equal(1, await context.EvaluationRounds.CountAsync());
        Assert.Equal(3, await context.BranchResults.CountAsync());
        Assert.Equal(1, await context.RemediationSubmissions.CountAsync());
        Assert.Equal(1, await context.WorkflowCorrelations.CountAsync());
    }

    [Fact]
    public async Task Approval_snapshot_and_response_are_atomic_idempotent_and_terminal()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var submission = CreateSubmission("release-approval");
        var evidence = CreateEveryEvidence(submission.Key);

        await using var context = database.CreateContext();
        var service = new ApplicationDataService(context);
        await service.SubmitReleaseAsync(
            submission,
            evidence,
            CreateTimeline(submission.Key, 1, TimelineEntryKind.ReleaseSubmitted, "Release submitted."),
            "approval:submit");
        var round = CreateRound(submission.Key, 1, evidence, passed: true);
        await service.SaveEvaluationRoundAsync(
            round,
            CreateTimeline(submission.Key, 2, TimelineEntryKind.EvaluationCompleted, "All checks passed."),
            "approval:round");

        var snapshot = new DecisionSnapshot(
            Guid.NewGuid(),
            submission.Key,
            round.Id,
            round.RoundNumber,
            round.Results,
            round.Results.Min(result => result.ValidUntil!.Value),
            "All readiness checks passed.",
            Utc(2026, 8, 16, 10));
        var request = new HumanDecisionRequest(
            Guid.NewGuid(), submission.Key, snapshot.Id, "snapshot-token", Utc(2026, 8, 16, 10));
        await service.OpenHumanDecisionRequestAsync(
            snapshot,
            request,
            CreateTimeline(submission.Key, 3, TimelineEntryKind.ApprovalRequested, "Approval requested."),
            "approval:request");
        await service.OpenHumanDecisionRequestAsync(
            snapshot,
            request,
            CreateTimeline(submission.Key, 3, TimelineEntryKind.ApprovalRequested, "Approval requested."),
            "approval:request");

        var response = new HumanResponse(
            Guid.NewGuid(), request.Id, snapshot.Id, request.ConcurrencyToken,
            HumanDecision.Approve, "coordinator", Utc(2026, 8, 16, 11));
        var validation = HumanResponseValidation.Accepted(response.Id, Utc(2026, 8, 16, 11));
        await service.SaveHumanResponseAsync(
            submission.Key,
            response,
            validation,
            CreateTimeline(submission.Key, 4, TimelineEntryKind.HumanResponseAccepted, "Release approved."),
            "approval:response");
        await service.SaveHumanResponseAsync(
            submission.Key,
            response,
            validation,
            CreateTimeline(submission.Key, 4, TimelineEntryKind.HumanResponseAccepted, "Release approved."),
            "approval:response");

        var detail = await service.GetReleaseDetailAsync(submission.Key);
        Assert.NotNull(detail);
        Assert.Equal(ProcessPhase.Approved, detail.Release.Phase);
        Assert.Single(detail.DecisionSnapshots);
        Assert.Single(detail.HumanDecisionRequests);
        Assert.Single(detail.HumanResponses);
        Assert.Equal(4, detail.Timeline.Length);
        Assert.Equal(1, await context.DecisionSnapshots.CountAsync());
        Assert.Equal(3, await context.DecisionSnapshotSources.CountAsync());
        Assert.Equal(1, await context.HumanResponses.CountAsync());
    }

    [Fact]
    public async Task Evaluation_round_replay_uses_the_composite_key_and_preserves_durable_generated_ids()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var submission = CreateSubmission("release-workflow-payload");
        var evidence = CreateEveryEvidence(submission.Key);

        await using var context = database.CreateContext();
        var service = new ApplicationDataService(context);
        await service.SubmitReleaseAsync(
            submission,
            evidence,
            CreateTimeline(submission.Key, 1, TimelineEntryKind.ReleaseSubmitted, "Release submitted."),
            "workflow-payload:submit");
        var round = CreateRound(submission.Key, 1, evidence, passed: true);
        await service.SaveEvaluationRoundAsync(
            round,
            CreateTimeline(submission.Key, 2, TimelineEntryKind.EvaluationCompleted, "Evaluation completed."),
            "workflow-payload:round");

        var replayAttempt = CreateRound(submission.Key, 1, evidence, passed: true);
        var replayed = await service.SaveEvaluationRoundAsync(
            replayAttempt,
            CreateTimeline(submission.Key, 2, TimelineEntryKind.EvaluationCompleted, "Evaluation completed."),
            "workflow-payload:round");

        Assert.Equal(round.Id, replayed.Id);
        Assert.NotEqual(replayAttempt.Id, replayed.Id);
        Assert.Equal(
            round.Results.Select(result => result.Id),
            replayed.Results.Select(result => result.Id));
        Assert.Equal(1, await context.EvaluationRounds.CountAsync());
        Assert.Equal(3, await context.BranchResults.CountAsync());
        Assert.Equal(2, await context.TimelineEntries.CountAsync());
    }

    [Fact]
    public async Task Reusing_a_workflow_operation_key_for_a_different_composite_key_fails_loudly()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var submission = CreateSubmission("release-workflow-composite-conflict");
        var evidence = CreateEveryEvidence(submission.Key);

        await using var context = database.CreateContext();
        var service = new ApplicationDataService(context);
        await service.SubmitReleaseAsync(
            submission,
            evidence,
            CreateTimeline(submission.Key, 1, TimelineEntryKind.ReleaseSubmitted, "Release submitted."),
            "workflow-composite-conflict:submit");
        var firstRound = CreateRound(submission.Key, 1, evidence, passed: true);
        await service.SaveEvaluationRoundAsync(
            firstRound,
            CreateTimeline(submission.Key, 2, TimelineEntryKind.EvaluationCompleted, "Round 1 completed."),
            "workflow-composite-conflict:round");

        var secondRound = CreateRound(submission.Key, 2, evidence, passed: true);
        var conflict = await Assert.ThrowsAsync<ApplicationDataConflictException>(() =>
            service.SaveEvaluationRoundAsync(
                secondRound,
                CreateTimeline(submission.Key, 3, TimelineEntryKind.EvaluationCompleted, "Round 2 completed."),
                "workflow-composite-conflict:round"));

        Assert.Equal(ApplicationDataConflictKind.OperationKeyReused, conflict.Kind);
    }

    private static ChangeEvidenceRecord CreateChangeEvidence(ReleaseRevisionKey key, Guid id) => new(
        id,
        key,
        1,
        Utc(2026, 8, 16, 8),
        null,
        false,
        null);

    private static EvidenceRecord[] CreateEveryEvidence(ReleaseRevisionKey key) =>
    [
        CreateTestEvidence(key, Guid.NewGuid(), 1, null, 0.99m),
        new SecurityEvidenceRecord(
            Guid.NewGuid(), key, 1, Utc(2026, 8, 16, 8), null,
            "security-1", Utc(2026, 8, 16, 8), [], [], null),
        new ChangeEvidenceRecord(
            Guid.NewGuid(), key, 1, Utc(2026, 8, 16, 8), null,
            true,
            new UtcInterval(Utc(2026, 8, 17, 8), Utc(2026, 8, 17, 9))),
    ];

    private static EvaluationRound CreateRound(
        ReleaseRevisionKey key,
        int number,
        TestEvidenceRecord testEvidence,
        ChangeEvidenceRecord changeEvidence,
        bool passed) =>
        CreateRound(key, number, [testEvidence, changeEvidence], passed);

    private static EvaluationRound CreateRound(
        ReleaseRevisionKey key,
        int number,
        IReadOnlyCollection<EvidenceRecord> evidence,
        bool passed)
    {
        var evidenceByKind = evidence.ToDictionary(item => item.Kind);
        var results = Enum.GetValues<ReadinessCheck>().Select(check =>
        {
            var kind = check switch
            {
                ReadinessCheck.Test => EvidenceKind.Test,
                ReadinessCheck.Security => EvidenceKind.Security,
                ReadinessCheck.Change => EvidenceKind.Change,
                _ => throw new ArgumentOutOfRangeException(nameof(check)),
            };
            evidenceByKind.TryGetValue(kind, out var source);
            var outcome = passed ? BranchOutcome.Passed
                : source is null ? BranchOutcome.MissingEvidence : BranchOutcome.Blocked;
            return new BranchResult(
                Guid.NewGuid(),
                key,
                number,
                check,
                outcome,
                ExecutionDisposition.Executed,
                PlanningReason.InitialEvaluation,
                "Executed because this is the initial evaluation.",
                source?.Id,
                kind,
                passed ? Utc(2026, 8, 17, 12 + (int)check) : null,
                ["attempt-1"],
                new Dictionary<string, string> { ["summary"] = outcome.ToString() },
                null,
                null);
        });

        return new EvaluationRound(
            Guid.NewGuid(), key, number, Utc(2026, 8, 16, 9), Utc(2026, 8, 16, 10), results);
    }

    private static ReleaseSubmission CreateSubmission(string releaseId) => new(
        new ReleaseRevisionKey(releaseId, 1),
        "orders",
        "1.0.0",
        new UtcInterval(
            Utc(2026, 8, 17, 8),
            Utc(2026, 8, 17, 9)),
        Utc(2026, 8, 16, 8));

    private static TestEvidenceRecord CreateTestEvidence(
        ReleaseRevisionKey key,
        Guid id,
        int version,
        Guid? supersedes,
        decimal passRate) => new(
            id,
            key,
            version,
            Utc(2026, 8, 16, 8 + version),
            supersedes,
            "1.0.0",
            Utc(2026, 8, 16, 7 + version),
            passRate,
            []);

    private static TimelineEntry CreateTimeline(
        ReleaseRevisionKey key,
        long sequence,
        TimelineEntryKind kind,
        string summary) => new(
            Guid.NewGuid(),
            key,
            sequence,
            kind,
            summary,
            Utc(2026, 8, 16, 8 + (int)sequence));

    private static UtcInstant Utc(int year, int month, int day, int hour) =>
        new(new DateTimeOffset(year, month, day, hour, 0, 0, TimeSpan.Zero));

    private sealed class TemporaryDatabase : IAsyncDisposable
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
                    $"release-readiness-data-{Guid.NewGuid():N}.db"));
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
}
