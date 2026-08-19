using System.Collections.Immutable;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using ReleaseReadinessCoordinator.Data;
using ReleaseReadinessCoordinator.Domain;
using ReleaseReadinessCoordinator.Pages.Releases;

namespace ReleaseReadinessCoordinator.Tests.Web;

public sealed class ReleaseDetailPageTests
{
    private static readonly UtcInstant SubmittedAt =
        new(new DateTimeOffset(2026, 8, 19, 8, 0, 0, TimeSpan.Zero));
    private static readonly UtcInstant CompletedAt =
        new(new DateTimeOffset(2026, 8, 19, 8, 5, 0, TimeSpan.Zero));
    private static readonly UtcInstant ValidUntil =
        new(new DateTimeOffset(2026, 8, 20, 8, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Full_detail_renders_current_results_evidence_round_reasons_wait_brief_and_timeline()
    {
        var projection = CreateApprovalProjection();
        await using var page = await RenderedPage.StartAsync(projection);

        var response = await page.Client.GetAsync("/Releases/release-ui");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("<h1", html, StringComparison.Ordinal);
        Assert.Contains("Release release-ui", html, StringComparison.Ordinal);
        Assert.Contains("<h2", html, StringComparison.Ordinal);
        Assert.Contains("Current results", html, StringComparison.Ordinal);
        Assert.Contains("Test", html, StringComparison.Ordinal);
        Assert.Contains("Security", html, StringComparison.Ordinal);
        Assert.Contains("Change", html, StringComparison.Ordinal);
        Assert.Contains("Passed", html, StringComparison.Ordinal);
        Assert.Contains("Valid until", html, StringComparison.Ordinal);
        Assert.Contains("provider attempt 1 succeeded", html, StringComparison.Ordinal);
        Assert.Contains("coverage", html, StringComparison.Ordinal);
        Assert.Contains("99%", html, StringComparison.Ordinal);
        Assert.Contains("Evidence history", html, StringComparison.Ordinal);
        Assert.Contains("test-run-42", html, StringComparison.Ordinal);
        Assert.Contains("Evaluation rounds", html, StringComparison.Ordinal);
        Assert.Contains("Executed because initial evaluation", html, StringComparison.Ordinal);
        Assert.Contains("Reused from round 1 because the passing result is still current", html, StringComparison.Ordinal);
        Assert.Contains("href=\"#round-1\"", html, StringComparison.Ordinal);
        Assert.Contains("Ready for an immutable approval decision.", html, StringComparison.Ordinal);
        Assert.Contains("Active wait", html, StringComparison.Ordinal);
        Assert.Contains("Awaiting approval", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("href=\"/Releases/release-ui/Decision\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("workflow-session-secret", html, StringComparison.Ordinal);
        Assert.DoesNotContain("workflow-request-secret", html, StringComparison.Ordinal);
        Assert.Contains("<th scope=\"col\"", html, StringComparison.Ordinal);

        var submitted = html.IndexOf("Release submitted", StringComparison.Ordinal);
        var evaluated = html.IndexOf("Round 2 completed", StringComparison.Ordinal);
        var approval = html.IndexOf("Approval requested", StringComparison.Ordinal);
        Assert.True(submitted >= 0 && evaluated > submitted && approval > evaluated);
    }

    [Fact]
    public async Task Pre_evaluation_detail_names_all_three_checks_and_has_meaningful_empty_states()
    {
        var projection = CreateProjection(ProcessPhase.Evaluating);
        await using var page = await RenderedPage.StartAsync(projection);

        var response = await page.Client.GetAsync("/Releases/release-ui");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Test", html, StringComparison.Ordinal);
        Assert.Contains("Security", html, StringComparison.Ordinal);
        Assert.Contains("Change", html, StringComparison.Ordinal);
        Assert.Equal(3, Count(html, "Not evaluated"));
        Assert.Contains("No evidence has been supplied", html, StringComparison.Ordinal);
        Assert.Contains("No evaluation rounds have completed", html, StringComparison.Ordinal);
        Assert.Contains("No decision brief is available", html, StringComparison.Ordinal);
        Assert.Contains("No active workflow wait", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failed_detail_shows_safe_failure_category_without_raw_diagnostic_or_stale_wait_ids()
    {
        var releaseId = new ReleaseId("release-ui");
        var unsafeFailure = new TimelineEntry(
            Guid.NewGuid(),
            releaseId,
            2,
            TimelineEntryKind.WorkflowFailed,
            @"Workflow failed (Corrupt): checkpoint C:\private\super-secret-token.json contained raw payload secret=abc123.",
            CompletedAt);
        var staleCorrelation = new WorkflowCorrelationRecord(
            releaseId,
            "workflow-session-secret",
            "workflow-request-secret",
            WorkflowRequestKind.Approval,
            CompletedAt);
        var projection = CreateProjection(
            ProcessPhase.Failed,
            workflowCorrelation: staleCorrelation,
            timeline:
            [
                Timeline(releaseId, 1, TimelineEntryKind.ReleaseSubmitted, "Release submitted", SubmittedAt),
                unsafeFailure,
            ]);
        await using var page = await RenderedPage.StartAsync(projection);

        var response = await page.Client.GetAsync("/Releases/release-ui");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Workflow failed (Corrupt)", html, StringComparison.Ordinal);
        Assert.Contains("diagnostic details are available in application logs", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("super-secret-token", html, StringComparison.Ordinal);
        Assert.DoesNotContain("abc123", html, StringComparison.Ordinal);
        Assert.DoesNotContain("workflow-session-secret", html, StringComparison.Ordinal);
        Assert.DoesNotContain("workflow-request-secret", html, StringComparison.Ordinal);
        Assert.Contains("No active workflow wait", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Remediation_detail_announces_the_active_wait_without_exposing_correlation_ids()
    {
        var releaseId = new ReleaseId("release-ui");
        var projection = CreateProjection(
            ProcessPhase.WaitingForRemediation,
            workflowCorrelation: new WorkflowCorrelationRecord(
                releaseId,
                "workflow-session-secret",
                "workflow-request-secret",
                WorkflowRequestKind.Remediation,
                CompletedAt));
        await using var page = await RenderedPage.StartAsync(projection);

        var response = await page.Client.GetAsync("/Releases/release-ui");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("WaitingForRemediation", html, StringComparison.Ordinal);
        Assert.Contains("Awaiting remediation response", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("href=\"/Releases/release-ui/Remediate\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("workflow-session-secret", html, StringComparison.Ordinal);
        Assert.DoesNotContain("workflow-request-secret", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Terminal_detail_renders_the_audited_decision_and_ignores_stale_wait_correlation()
    {
        var waiting = CreateApprovalProjection();
        var approvalRequest = Assert.Single(waiting.HumanDecisionRequests);
        var respondedAt = new UtcInstant(ValidUntil.Value.AddMinutes(1));
        var responseRecord = new PersistedHumanResponse(
            waiting.Release.Submission.ReleaseId,
            approvalRequest.Id,
            new HumanResponse(
                Guid.NewGuid(),
                HumanDecision.Approve,
                "release-manager",
                "Approved for the requested window.",
                respondedAt));
        var projection = waiting with
        {
            Release = waiting.Release.TransitionTo(ProcessPhase.Approved, respondedAt),
            TerminalResponse = responseRecord,
        };
        await using var page = await RenderedPage.StartAsync(projection);

        var response = await page.Client.GetAsync("/Releases/release-ui");
        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Terminal decision", html, StringComparison.Ordinal);
        Assert.Contains("release-manager", html, StringComparison.Ordinal);
        Assert.Contains("Approved for the requested window.", html, StringComparison.Ordinal);
        Assert.Contains("No active workflow wait", html, StringComparison.Ordinal);
        Assert.DoesNotContain("workflow-session-secret", html, StringComparison.Ordinal);
        Assert.DoesNotContain("workflow-request-secret", html, StringComparison.Ordinal);
    }

    private static ReleaseDetailProjection CreateApprovalProjection()
    {
        var releaseId = new ReleaseId("release-ui");
        var testEvidence = new TestEvidenceRecord(
            Guid.NewGuid(), releaseId, 1, SubmittedAt, null,
            "test-run-42", SubmittedAt, 0.99m, []);
        var securityEvidence = new SecurityEvidenceRecord(
            Guid.NewGuid(), releaseId, 1, SubmittedAt, null,
            "scan-42", SubmittedAt, [], [],
            new Dictionary<string, (string Scope, UtcInstant ExpiresAt)>());
        var changeEvidence = new ChangeEvidenceRecord(
            Guid.NewGuid(), releaseId, 1, SubmittedAt, null, true,
            new UtcInterval(SubmittedAt, ValidUntil));

        var roundOneResults = new[]
        {
            ExecutedResult(releaseId, 1, ReadinessCheck.Test, testEvidence.Id, "Executed because initial evaluation", "coverage", "99%"),
            ExecutedResult(releaseId, 1, ReadinessCheck.Security, securityEvidence.Id, "Executed because initial evaluation"),
            ExecutedResult(releaseId, 1, ReadinessCheck.Change, changeEvidence.Id, "Executed because initial evaluation"),
        };
        var roundOne = new EvaluationRound(
            Guid.NewGuid(), releaseId, 1, SubmittedAt, CompletedAt, roundOneResults);
        var roundTwoResults = new[]
        {
            ExecutedResult(releaseId, 2, ReadinessCheck.Test, testEvidence.Id, "Executed because explicitly selected", "coverage", "99%"),
            ReusedResult(releaseId, 2, roundOneResults[1]),
            ReusedResult(releaseId, 2, roundOneResults[2]),
        };
        var roundTwo = new EvaluationRound(
            Guid.NewGuid(), releaseId, 2, CompletedAt, ValidUntil, roundTwoResults);
        var snapshot = new DecisionSnapshot(
            Guid.NewGuid(), releaseId, roundTwo.Id, 2, roundTwoResults,
            ValidUntil, "Ready for an immutable approval decision.", ValidUntil);
        var approvalRequest = new HumanDecisionRequest(
            Guid.NewGuid(), releaseId, snapshot.Id, ValidUntil);

        return CreateProjection(
            ProcessPhase.WaitingForApproval,
            evidenceHistory: [testEvidence, securityEvidence, changeEvidence],
            currentEvidence: new Dictionary<EvidenceKind, EvidenceRecord>
            {
                [EvidenceKind.Test] = testEvidence,
                [EvidenceKind.Security] = securityEvidence,
                [EvidenceKind.Change] = changeEvidence,
            },
            rounds: [roundOne, roundTwo],
            snapshots: [snapshot],
            approvalRequests: [approvalRequest],
            workflowCorrelation: new WorkflowCorrelationRecord(
                releaseId,
                "workflow-session-secret",
                "workflow-request-secret",
                WorkflowRequestKind.Approval,
                ValidUntil),
            timeline:
            [
                Timeline(releaseId, 1, TimelineEntryKind.ReleaseSubmitted, "Release submitted", SubmittedAt),
                Timeline(releaseId, 2, TimelineEntryKind.EvaluationCompleted, "Round 2 completed", CompletedAt),
                Timeline(releaseId, 3, TimelineEntryKind.ApprovalRequested, "Approval requested", ValidUntil),
            ]);
    }

    private static ReleaseDetailProjection CreateProjection(
        ProcessPhase phase,
        IEnumerable<EvidenceRecord>? evidenceHistory = null,
        IReadOnlyDictionary<EvidenceKind, EvidenceRecord>? currentEvidence = null,
        IEnumerable<EvaluationRound>? rounds = null,
        IEnumerable<DecisionSnapshot>? snapshots = null,
        IEnumerable<HumanDecisionRequest>? approvalRequests = null,
        WorkflowCorrelationRecord? workflowCorrelation = null,
        IEnumerable<TimelineEntry>? timeline = null)
    {
        var releaseId = new ReleaseId("release-ui");
        var submission = new ReleaseSubmission(
            releaseId,
            "payments-api",
            "2026.08.19",
            new UtcInterval(SubmittedAt, ValidUntil),
            SubmittedAt);
        var release = phase == ProcessPhase.Evaluating
            ? Release.Create(submission)
            : Release.Create(submission).TransitionTo(phase, CompletedAt);

        return new ReleaseDetailProjection(
            release,
            [.. evidenceHistory ?? []],
            (currentEvidence ?? new Dictionary<EvidenceKind, EvidenceRecord>()).ToImmutableDictionary(),
            [.. rounds ?? []],
            [],
            [],
            [.. snapshots ?? []],
            [.. approvalRequests ?? []],
            null,
            workflowCorrelation,
            [.. timeline ?? []]);
    }

    private static BranchResult ExecutedResult(
        ReleaseId releaseId,
        int round,
        ReadinessCheck check,
        Guid evidenceId,
        string planningDetail,
        string? findingKey = null,
        string? findingValue = null) =>
        new(
            Guid.NewGuid(), releaseId, round, check, BranchOutcome.Passed,
            ExecutionDisposition.Executed,
            round == 1 ? PlanningReason.InitialEvaluation : PlanningReason.ExplicitlySelected,
            planningDetail,
            evidenceId,
            (EvidenceKind)(int)check,
            ValidUntil,
            ["provider attempt 1 succeeded"],
            findingKey is null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string> { [findingKey] = findingValue! },
            null,
            null);

    private static BranchResult ReusedResult(
        ReleaseId releaseId,
        int round,
        BranchResult source) =>
        new(
            Guid.NewGuid(), releaseId, round, source.Check, BranchOutcome.Passed,
            ExecutionDisposition.Reused,
            PlanningReason.StillCurrent,
            $"Reused from round {source.RoundNumber} because the passing result is still current",
            source.EvidenceId,
            source.EvidenceKind,
            source.ValidUntil,
            [],
            source.Findings,
            source.Id,
            source.RoundNumber);

    private static TimelineEntry Timeline(
        ReleaseId releaseId,
        long sequence,
        TimelineEntryKind kind,
        string summary,
        UtcInstant occurredAt) =>
        new(Guid.NewGuid(), releaseId, sequence, kind, summary, occurredAt);

    private static int Count(string value, string target)
    {
        var count = 0;
        var index = 0;
        while ((index = value.IndexOf(target, index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += target.Length;
        }

        return count;
    }

    private sealed class RenderedPage : IAsyncDisposable
    {
        private readonly WebApplication _application;

        private RenderedPage(WebApplication application, HttpClient client)
        {
            _application = application;
            Client = client;
        }

        public HttpClient Client { get; }

        public static async Task<RenderedPage> StartAsync(ReleaseDetailProjection projection)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(DetailModel).Assembly.FullName,
                EnvironmentName = "Development",
            });
            builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
            builder.Services.AddRazorPages();
            builder.Services.AddSingleton<IApplicationDataService>(new ProjectionDataService(projection));

            var application = builder.Build();
            application.MapRazorPages();
            await application.StartAsync();
            var address = application.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!
                .Addresses.Single();
            return new RenderedPage(
                application,
                new HttpClient { BaseAddress = new Uri(address) });
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _application.DisposeAsync();
        }
    }

    private sealed class ProjectionDataService(ReleaseDetailProjection projection) : IApplicationDataService
    {
        public Task<ReleaseDetailProjection?> GetReleaseDetailAsync(
            ReleaseId releaseId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<ReleaseDetailProjection?>(
                releaseId == projection.Release.Submission.ReleaseId ? projection : null);

        public Task<EvidenceRecord?> GetCurrentEvidenceAsync(ReleaseId releaseId, EvidenceKind kind, CancellationToken cancellationToken = default) => throw Unused();
        public Task<Release> SubmitReleaseAsync(ReleaseSubmission submission, IReadOnlyCollection<EvidenceRecord> initialEvidence, TimelineEntry timelineEntry, string operationKey, CancellationToken cancellationToken = default) => throw Unused();
        public Task<EvidenceRecord> ReplaceEvidenceAsync(EvidenceRecord evidence, TimelineEntry timelineEntry, string operationKey, CancellationToken cancellationToken = default) => throw Unused();
        public Task<EvaluationRound> SaveEvaluationRoundAsync(EvaluationRound round, TimelineEntry timelineEntry, string operationKey, CancellationToken cancellationToken = default) => throw Unused();
        public Task<RemediationRequest> OpenRemediationRequestAsync(RemediationRequest request, TimelineEntry timelineEntry, string operationKey, CancellationToken cancellationToken = default) => throw Unused();
        public Task<RemediationSubmission> SaveRemediationSubmissionAsync(ReleaseId releaseId, RemediationSubmission submission, IReadOnlyCollection<EvidenceRecord> evidenceReplacements, TimelineEntry timelineEntry, string operationKey, CancellationToken cancellationToken = default) => throw Unused();
        public Task<HumanDecisionRequest> OpenHumanDecisionRequestAsync(DecisionSnapshot snapshot, HumanDecisionRequest request, TimelineEntry timelineEntry, string operationKey, CancellationToken cancellationToken = default) => throw Unused();
        public Task<PersistedHumanResponse> SaveHumanResponseAsync(ReleaseId releaseId, Guid approvalRequestId, HumanResponse response, TimelineEntry timelineEntry, string operationKey, CancellationToken cancellationToken = default) => throw Unused();
        public Task<WorkflowCorrelationRecord> SaveWorkflowCorrelationAsync(WorkflowCorrelationRecord correlation, string operationKey, CancellationToken cancellationToken = default) => throw Unused();
        public Task<Release> MarkWorkflowFailedAsync(ReleaseId releaseId, UtcInstant failedAt, TimelineEntry timelineEntry, string operationKey, CancellationToken cancellationToken = default) => throw Unused();
        public Task<TimelineEntry> AppendTimelineEntryAsync(TimelineEntry timelineEntry, string operationKey, CancellationToken cancellationToken = default) => throw Unused();

        private static InvalidOperationException Unused() =>
            new("This detail-page test only reads the release-detail projection.");
    }
}
