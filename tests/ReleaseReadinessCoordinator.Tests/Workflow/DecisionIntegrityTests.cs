using System.Text;
using Microsoft.EntityFrameworkCore;
using ReleaseReadinessCoordinator.Data;
using ReleaseReadinessCoordinator.Domain;
using ReleaseReadinessCoordinator.Readiness;
using ReleaseReadinessCoordinator.Tests.Readiness;
using ReleaseReadinessCoordinator.Tests.Support;
using ReleaseReadinessCoordinator.Workflow;

namespace ReleaseReadinessCoordinator.Tests.Workflow;

public sealed class DecisionIntegritySnapshotTests
{
    [Fact]
    public async Task Fully_passing_round_persists_one_snapshot_request_and_byte_stable_brief()
    {
        await using var database = await TemporaryDatabase.CreateAsync();
        var submission = Submission("decision-snapshot");
        var evidence = Evidence(submission);

        await using var context = database.CreateContext();
        var dataService = new ApplicationDataService(context);
        await SubmitAndSaveRoundAsync(dataService, submission, evidence);
        var detail = await dataService.GetReleaseDetailAsync(submission.ReleaseId);
        var round = Assert.Single(detail!.EvaluationRounds);
        var timeProvider = new FixedTimeProvider(Utc(2026, 8, 17, 10, 5).Value);
        var builder = new DecisionSnapshotBuilder(submission.ReleaseId, dataService, timeProvider);

        var first = await builder.BuildAsync(round);
        var replay = await builder.BuildAsync(round);

        Assert.Equal(first.Snapshot.Id, replay.Snapshot.Id);
        Assert.Equal(first.Request.Id, replay.Request.Id);
        Assert.Equal(round.Results.Select(result => result.Id), first.Snapshot.Sources.Select(result => result.Id));
        Assert.Equal(round.Results.Select(result => result.EvidenceId), first.Snapshot.Sources.Select(result => result.EvidenceId));
        Assert.Equal(
            Encoding.UTF8.GetBytes(DecisionSnapshotBuilder.BuildBrief(round)),
            Encoding.UTF8.GetBytes(first.Snapshot.DecisionBrief));
        Assert.DoesNotContain("valid", first.Snapshot.DecisionBrief, StringComparison.OrdinalIgnoreCase);

        detail = await dataService.GetReleaseDetailAsync(submission.ReleaseId);
        Assert.Single(detail!.DecisionSnapshots);
        Assert.Single(detail.HumanDecisionRequests);
        Assert.Equal(ProcessPhase.WaitingForApproval, detail.Release.Phase);
    }

    private static async Task SubmitAndSaveRoundAsync(
        IApplicationDataService dataService,
        ReleaseSubmission submission,
        IReadOnlyList<EvidenceRecord> evidence)
    {
        await dataService.SubmitReleaseAsync(
            submission,
            evidence,
            Timeline(submission.ReleaseId, 1, TimelineEntryKind.ReleaseSubmitted, "Release submitted."),
            $"decision:{submission.ReleaseId.Value}:submit");
        var round = Round(submission, evidence);
        await dataService.SaveEvaluationRoundAsync(
            round,
            Timeline(submission.ReleaseId, 2, TimelineEntryKind.EvaluationCompleted, "Round completed."),
            $"decision:{submission.ReleaseId.Value}:round:1");
    }

    private static EvaluationRound Round(
        ReleaseSubmission submission,
        IReadOnlyList<EvidenceRecord> evidence) => new(
        Guid.NewGuid(),
        submission.ReleaseId,
        1,
        Utc(2026, 8, 17, 10),
        Utc(2026, 8, 17, 10, 1),
        [
            Result(ReadinessCheck.Test, evidence[0]),
            Result(ReadinessCheck.Security, evidence[1]),
            Result(ReadinessCheck.Change, evidence[2]),
        ]);

    private static BranchResult Result(
        ReadinessCheck check,
        EvidenceRecord evidence) => new(
        Guid.NewGuid(),
        evidence.ReleaseId,
        1,
        check,
        BranchOutcome.Passed,
        ExecutionDisposition.Executed,
        PlanningReason.InitialEvaluation,
        "Executed because no prior result exists.",
        evidence.Id,
        evidence.Kind,
        ["Attempt 1 succeeded."],
        new Dictionary<string, string> { ["ready"] = "Passed." },
        null,
        null);

    private static ReleaseSubmission Submission(string releaseId) => new(
        new ReleaseId(releaseId),
        "orders",
        "2.4.0",
        new UtcInterval(Utc(2026, 8, 17, 10), Utc(2026, 8, 17, 11)),
        Utc(2026, 8, 17, 9));

    private static EvidenceRecord[] Evidence(ReleaseSubmission submission) =>
    [
        new TestEvidenceRecord(
            Guid.NewGuid(), submission.ReleaseId, 1, submission.SubmittedAt, null,
            submission.ReleaseVersion, submission.SubmittedAt, 0.99m, []),
        new SecurityEvidenceRecord(
            Guid.NewGuid(), submission.ReleaseId, 1, submission.SubmittedAt, null,
            submission.ReleaseVersion, submission.SubmittedAt, [], [],
            new Dictionary<string, (string Scope, UtcInstant ExpiresAt)>()),
        new ChangeEvidenceRecord(
            Guid.NewGuid(), submission.ReleaseId, 1, submission.SubmittedAt, null,
            true, submission.RequestedDeploymentWindow),
    ];

    private static TimelineEntry Timeline(
        ReleaseId key,
        long sequence,
        TimelineEntryKind kind,
        string summary) => new(Guid.NewGuid(), key, sequence, kind, summary, Utc(2026, 8, 17, 10, 1));

    private static UtcInstant Utc(int year, int month, int day, int hour, int minute = 0) =>
        new(new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.Zero));

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}

public sealed class DecisionIntegrityResponseTests
{
    [Fact]
    public async Task Reusing_a_response_id_with_different_content_fails()
    {
        await using var fixture = await DecisionFixture.CreateAsync("conflicting-response-id");
        var response = fixture.Response(HumanDecision.Approve);
        var handler = new HumanDecisionHandler(fixture.Submission.ReleaseId, fixture.DataService);
        var first = await handler.HandleAsync(response);
        var replay = await handler.HandleAsync(response);
        var conflict = new HumanResponse(
            response.Id,
            HumanDecision.Reject,
            response.Responder,
            "Conflicting decision.",
            response.RespondedAt);

        Assert.Equal(first, replay);
        await Assert.ThrowsAsync<InvalidOperationException>(() => handler.HandleAsync(conflict));

        var detail = await fixture.DataService.GetReleaseDetailAsync(fixture.Submission.ReleaseId);
        Assert.Equal(ProcessPhase.Approved, detail!.Release.Phase);
        Assert.Equal(response, detail.TerminalResponse!.Response);
    }

    [Fact]
    public async Task Approval_boundary_preserves_the_snapshot_and_rejects_workflow_input_changes()
    {
        await using var fixture = await DecisionFixture.CreateAsync("closed-snapshot");
        var originalBrief = fixture.Snapshot.DecisionBrief;

        var remediationConflict = await Assert.ThrowsAsync<ApplicationDataConflictException>(
            fixture.SubmitRemediationWithEvidenceAsync);
        var rerunConflict = await Assert.ThrowsAsync<ApplicationDataConflictException>(
            fixture.SaveRerunAsync);

        Assert.All(
            [remediationConflict, rerunConflict],
            conflict => Assert.Equal(ApplicationDataConflictKind.InvalidState, conflict.Kind));

        fixture.Clock.SetUtcNow(
            new DateTimeOffset(2026, 8, 18, 12, 0, 0, TimeSpan.Zero));
        var handler = new HumanDecisionHandler(fixture.Submission.ReleaseId, fixture.DataService);

        await handler.HandleAsync(fixture.Response(HumanDecision.Approve));

        var detail = await fixture.DataService.GetReleaseDetailAsync(fixture.Submission.ReleaseId);
        Assert.Equal(ProcessPhase.Approved, detail!.Release.Phase);
        Assert.Equal(originalBrief, Assert.Single(detail.DecisionSnapshots).DecisionBrief);
        Assert.Single(detail.EvaluationRounds);
        Assert.Empty(detail.RemediationSubmissions);
        Assert.Single(detail.EvidenceHistory.Where(item => item.Kind is EvidenceKind.Test));
        Assert.NotNull(detail.TerminalResponse);
    }
}

public sealed class DecisionIntegrityRealGraphTests
{
    [Fact]
    public async Task Invalid_continuation_is_rejected_before_creating_a_human_response()
    {
        await using var host = await ReadinessWorkflowTestHost.CreateForOutcomesAsync(BranchOutcome.Passed);
        using var checkpoints = new DecisionCheckpointDirectory();
        using var coordinator = new CheckpointStoreCoordinator(checkpoints.Info);
        const string SessionId = "invalid-decision-continuation";
        var pending = Assert.IsType<PendingApprovalWait>(
            await coordinator.StartAsync(host.CreateWorkflow(), host.Input, SessionId));
        var mismatch = pending with { WorkflowRequestId = Guid.NewGuid().ToString("N") };

        var conflict = await Assert.ThrowsAsync<WorkflowContinuationException>(() =>
            coordinator.ResumeApprovalAsync(
                host.CreateWorkflow(), SessionId, mismatch, Response(host.Input.StartedAt, HumanDecision.Approve)));

        Assert.Equal(ContinuationFailureKind.Mismatched, conflict.Kind);
        var detail = await host.DataService.GetReleaseDetailAsync(host.Submission.ReleaseId);
        Assert.Equal(ProcessPhase.WaitingForApproval, detail!.Release.Phase);
        Assert.Null(detail.TerminalResponse);
    }

    private static ApprovalResponse Response(UtcInstant respondedAt, HumanDecision decision) =>
        new(new HumanResponse(
            Guid.NewGuid(),
            decision,
            "release-manager",
            "Reviewed the immutable decision brief.",
            respondedAt));
}

internal sealed class DecisionCheckpointDirectory : IDisposable
{
    public DecisionCheckpointDirectory()
    {
        Info = Directory.CreateDirectory(
            Path.Combine(Path.GetTempPath(), "decision-integrity-checkpoints", Guid.NewGuid().ToString("N")));
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

internal sealed class DecisionFixture : IAsyncDisposable
{
    private readonly TemporaryDatabase _database;

    private DecisionFixture(
        TemporaryDatabase database,
        AppDbContext context,
        ReleaseSubmission submission,
        ApplicationDataService dataService,
        MutableTimeProvider clock,
        DecisionSnapshot snapshot,
        HumanDecisionRequest request)
    {
        _database = database;
        Context = context;
        Submission = submission;
        DataService = dataService;
        Clock = clock;
        Snapshot = snapshot;
        Request = request;
    }

    public AppDbContext Context { get; }

    public ReleaseSubmission Submission { get; }

    public ApplicationDataService DataService { get; }

    public MutableTimeProvider Clock { get; }

    public DecisionSnapshot Snapshot { get; }

    public HumanDecisionRequest Request { get; }

    public static async Task<DecisionFixture> CreateAsync(string releaseId)
    {
        var database = await TemporaryDatabase.CreateAsync();
        var context = database.CreateContext();
        var dataService = new ApplicationDataService(context);
        var submission = new ReleaseSubmission(
            new ReleaseId(releaseId),
            "orders",
            "2.4.0",
            new UtcInterval(Utc(2026, 8, 17, 10), Utc(2026, 8, 17, 13)),
            Utc(2026, 8, 17, 9));
        var evidence = Evidence(submission);
        await dataService.SubmitReleaseAsync(
            submission,
            evidence,
            Timeline(submission.ReleaseId, 1, TimelineEntryKind.ReleaseSubmitted, "Release submitted."),
            $"fixture:{releaseId}:submit");
        var round = new EvaluationRound(
            Guid.NewGuid(),
            submission.ReleaseId,
            1,
            Utc(2026, 8, 17, 10),
            Utc(2026, 8, 17, 10, 1),
            [
                Result(ReadinessCheck.Test, evidence[0]),
                Result(ReadinessCheck.Security, evidence[1]),
                Result(ReadinessCheck.Change, evidence[2]),
            ]);
        await dataService.SaveEvaluationRoundAsync(
            round,
            Timeline(submission.ReleaseId, 2, TimelineEntryKind.EvaluationCompleted, "Round completed."),
            $"fixture:{releaseId}:round:1");
        var clock = new MutableTimeProvider(Utc(2026, 8, 17, 10, 5).Value);
        var built = await new DecisionSnapshotBuilder(submission.ReleaseId, dataService, clock).BuildAsync(round);
        return new DecisionFixture(
            database,
            context,
            submission,
            dataService,
            clock,
            built.Snapshot,
            built.Request);
    }

    public HumanResponse Response(HumanDecision decision) => new(
        Guid.NewGuid(),
        decision,
        "release-manager",
        "Reviewed the immutable decision brief.",
        new UtcInstant(Clock.GetUtcNow()));

    public async Task<RemediationSubmission> SubmitRemediationWithEvidenceAsync()
    {
        var current = (TestEvidenceRecord)(await DataService.GetCurrentEvidenceAsync(
            Submission.ReleaseId,
            EvidenceKind.Test))!;
        var replacement = new TestEvidenceRecord(
            Guid.NewGuid(), Submission.ReleaseId, 2, Utc(2026, 8, 17, 10, 6), current.Id,
            Submission.ReleaseVersion, Utc(2026, 8, 17, 10), 0.99m, []);
        return await DataService.SaveRemediationSubmissionAsync(
            Submission.ReleaseId,
            new RemediationSubmission(
                Guid.NewGuid(),
                Request.Id,
                new UtcInstant(Clock.GetUtcNow()),
                new Dictionary<EvidenceKind, Guid> { [EvidenceKind.Test] = replacement.Id },
                [ReadinessCheck.Test]),
            [replacement],
            Timeline(Submission.ReleaseId, 4, TimelineEntryKind.RemediationSubmitted, "Remediation submitted."),
            $"fixture:{Submission.ReleaseId.Value}:remediation");
    }

    public Task<EvaluationRound> SaveRerunAsync()
    {
        var startedAt = new UtcInstant(Clock.GetUtcNow());
        var round = new EvaluationRound(
            Guid.NewGuid(),
            Submission.ReleaseId,
            2,
            startedAt,
            startedAt,
            Snapshot.Sources.Select(source => new BranchResult(
                Guid.NewGuid(),
                Submission.ReleaseId,
                2,
                source.Check,
                source.Outcome,
                source.Disposition,
                source.PlanningReason,
                source.PlanningDetail,
                source.EvidenceId,
                source.EvidenceKind,
                source.Attempts,
                source.Findings,
                source.ReuseSourceResultId,
                source.ReuseSourceRound)));
        return DataService.SaveEvaluationRoundAsync(
            round,
            Timeline(Submission.ReleaseId, 4, TimelineEntryKind.EvaluationCompleted, "Rerun completed."),
            $"fixture:{Submission.ReleaseId.Value}:round:2");
    }

    public async ValueTask DisposeAsync()
    {
        await Context.DisposeAsync();
        await _database.DisposeAsync();
    }

    private static EvidenceRecord[] Evidence(ReleaseSubmission submission) =>
    [
        new TestEvidenceRecord(
            Guid.NewGuid(), submission.ReleaseId, 1, submission.SubmittedAt, null,
            submission.ReleaseVersion, submission.SubmittedAt, 0.99m, []),
        new SecurityEvidenceRecord(
            Guid.NewGuid(), submission.ReleaseId, 1, submission.SubmittedAt, null,
            submission.ReleaseVersion, submission.SubmittedAt, [], [],
            new Dictionary<string, (string Scope, UtcInstant ExpiresAt)>()),
        new ChangeEvidenceRecord(
            Guid.NewGuid(), submission.ReleaseId, 1, submission.SubmittedAt, null,
            true, submission.RequestedDeploymentWindow),
    ];

    private static BranchResult Result(
        ReadinessCheck check,
        EvidenceRecord evidence) => new(
        Guid.NewGuid(), evidence.ReleaseId, 1, check, BranchOutcome.Passed,
        ExecutionDisposition.Executed, PlanningReason.InitialEvaluation,
        "Executed because no prior result exists.", evidence.Id, evidence.Kind,
        ["Attempt 1 succeeded."],
        new Dictionary<string, string> { ["ready"] = "Passed." }, null, null);

    private static TimelineEntry Timeline(
        ReleaseId key,
        long sequence,
        TimelineEntryKind kind,
        string summary) => new(Guid.NewGuid(), key, sequence, kind, summary, Utc(2026, 8, 17, 10, 1));

    private static UtcInstant Utc(int year, int month, int day, int hour, int minute = 0) =>
        new(new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.Zero));
}

internal sealed class MutableTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private DateTimeOffset _utcNow = utcNow;

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void SetUtcNow(DateTimeOffset value) => _utcNow = value;
}
