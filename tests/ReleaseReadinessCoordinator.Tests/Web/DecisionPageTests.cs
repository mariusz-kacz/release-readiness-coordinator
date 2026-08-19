using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReleaseReadinessCoordinator.Domain;
using ReleaseReadinessCoordinator.Pages.Releases;
using ReleaseReadinessCoordinator.Tests.Readiness;
using ReleaseReadinessCoordinator.Workflow;

namespace ReleaseReadinessCoordinator.Tests.Web;

public sealed class DecisionPageTests
{
    private static readonly UtcInstant Now =
        new(new DateTimeOffset(2026, 8, 19, 9, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Active_checkpoint_request_renders_immutable_snapshot_and_only_decision_inputs()
    {
        var active = ActiveState();
        await using var page = await RenderedPage.StartAsync(
            new StubDecisionInteractionService(DecisionLoadResult.Active(active)));

        var response = await page.Client.GetAsync("/Releases/release-decision/Decision");
        var html = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"Expected an OK response but received {response.StatusCode}.\n{html}");
        Assert.Contains("Decision for release release-decision", html, StringComparison.Ordinal);
        Assert.Contains("Checkpoint-carried decision brief.", html, StringComparison.Ordinal);
        Assert.Contains(active.Approval.Snapshot.Id.ToString(), html, StringComparison.Ordinal);
        Assert.Contains(active.WorkflowRequestId, html, StringComparison.Ordinal);
        Assert.Contains("Test", html, StringComparison.Ordinal);
        Assert.Contains("Security", html, StringComparison.Ordinal);
        Assert.Contains("Change", html, StringComparison.Ordinal);
        Assert.Contains("coverage", html, StringComparison.Ordinal);
        Assert.Contains("99%", html, StringComparison.Ordinal);
        Assert.Contains("name=\"Input.ResponseId\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"Input.Decision\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"Input.Responder\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"Input.Comment\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"Input.SnapshotId\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"Input.WorkflowRequestId\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"Input.CorrelationToken\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"Input.ApprovalRequestId\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"__RequestVerificationToken\"", html, StringComparison.Ordinal);
        Assert.Contains("<fieldset", html, StringComparison.Ordinal);
        Assert.Contains("<legend", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Interaction_load_restores_the_active_typed_approval_request()
    {
        await using var harness = await WorkflowHarness.CreateAsync();

        var load = await harness.Interactions.LoadAsync(harness.ReleaseId);

        Assert.Equal(DecisionLoadOutcome.Active, load.Outcome);
        Assert.NotNull(load.Interaction);
        Assert.Equal(harness.Pending.WorkflowRequestId, load.Interaction.WorkflowRequestId);
        Assert.Equal(
            harness.Pending.Approval!.Request.Id,
            load.Interaction.Approval.Request.Id);
        Assert.Equal(
            harness.Pending.Approval.Snapshot.Id,
            load.Interaction.Approval.Snapshot.Id);
        Assert.Equal(
            harness.Pending.Approval.Snapshot.DecisionBrief,
            load.Interaction.Approval.Snapshot.DecisionBrief);
        Assert.Equal(
            harness.Pending.Approval.Snapshot.Sources.Select(source => source.Id),
            load.Interaction.Approval.Snapshot.Sources.Select(source => source.Id));
    }

    [Theory]
    [InlineData(HumanDecision.Approve, ProcessPhase.Approved)]
    [InlineData(HumanDecision.Reject, ProcessPhase.Rejected)]
    public async Task Exact_double_submit_has_one_terminal_decision(
        HumanDecision decision,
        ProcessPhase expectedPhase)
    {
        await using var harness = await WorkflowHarness.CreateAsync();
        var submission = new DecisionSubmission(
            Guid.NewGuid(),
            decision,
            "release-manager",
            "Reviewed the immutable checkpoint package.");

        var first = await harness.Interactions.SubmitAsync(
            harness.ReleaseId,
            submission);
        var replay = await harness.Interactions.SubmitAsync(
            harness.ReleaseId,
            submission);

        Assert.Equal(DecisionSubmitOutcome.Succeeded, first);
        Assert.Equal(DecisionSubmitOutcome.ExactReplay, replay);
        var detail = await harness.ReadAsync();
        Assert.NotNull(detail);
        Assert.Equal(expectedPhase, detail.Release.Phase);
        Assert.Equal(submission.ResponseId, detail.TerminalResponse!.Response.Id);
        Assert.Single(
            detail.Timeline,
            entry => entry.Kind is TimelineEntryKind.HumanResponseAccepted);
    }

    [Theory]
    [InlineData(HumanDecision.Approve)]
    [InlineData(HumanDecision.Reject)]
    public async Task Valid_decision_posts_only_audit_input_and_redirects_to_terminal_detail(
        HumanDecision decision)
    {
        var active = ActiveState();
        var service = new StubDecisionInteractionService(
            DecisionLoadResult.Active(active));
        var page = CreatePage(service);
        var responseId = Guid.NewGuid();
        page.Input = new DecisionModel.InputModel
        {
            ResponseId = responseId,
            Decision = decision,
            Responder = "  release-manager  ",
            Comment = "  Reviewed the immutable package.  ",
        };

        var result = await page.OnPostAsync(
            "release-decision",
            CancellationToken.None);

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("/Releases/Detail", redirect.PageName);
        Assert.Equal("release-decision", redirect.RouteValues!["releaseId"]);
        Assert.Contains(
            "terminal",
            Assert.IsType<string>(page.TempData[DecisionModel.FeedbackKey]),
            StringComparison.OrdinalIgnoreCase);
        var submitted = Assert.Single(service.Submissions);
        Assert.Equal(responseId, submitted.ResponseId);
        Assert.Equal(decision, submitted.Decision);
        Assert.Equal("release-manager", submitted.Responder);
        Assert.Equal("Reviewed the immutable package.", submitted.Comment);
    }

    [Fact]
    public async Task Whitespace_actor_and_comment_rerender_without_submitting()
    {
        var active = ActiveState();
        var service = new StubDecisionInteractionService(
            DecisionLoadResult.Active(active));
        var page = CreatePage(service);
        page.Input = new DecisionModel.InputModel
        {
            ResponseId = Guid.NewGuid(),
            Decision = HumanDecision.Approve,
            Responder = "  ",
            Comment = "\t",
        };

        var result = await page.OnPostAsync(
            "release-decision",
            CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.Same(active, page.State);
        Assert.False(page.ModelState.IsValid);
        Assert.Empty(service.Submissions);
    }

    [Fact]
    public async Task Empty_response_id_and_undefined_decision_rerender_without_submitting()
    {
        var service = new StubDecisionInteractionService(
            DecisionLoadResult.Active(ActiveState()));
        var page = CreatePage(service);
        page.Input = new DecisionModel.InputModel
        {
            ResponseId = Guid.Empty,
            Decision = (HumanDecision)999,
            Responder = "release-manager",
            Comment = "Reviewed.",
        };

        var result = await page.OnPostAsync(
            "release-decision",
            CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.False(page.ModelState.IsValid);
        Assert.Empty(service.Submissions);
    }

    [Theory]
    [InlineData(DecisionSubmitOutcome.NoLongerActive, "no longer active")]
    [InlineData(DecisionSubmitOutcome.ResponseConflict, "no longer matches")]
    [InlineData(DecisionSubmitOutcome.TechnicalFailure, "could not continue safely")]
    public async Task Unsafe_submission_outcomes_redirect_with_safe_feedback(
        DecisionSubmitOutcome outcome,
        string expectedFeedback)
    {
        var service = new StubDecisionInteractionService(
            DecisionLoadResult.Active(ActiveState()),
            outcome);
        var page = CreatePage(service);
        page.Input = new DecisionModel.InputModel
        {
            ResponseId = Guid.NewGuid(),
            Decision = HumanDecision.Approve,
            Responder = "release-manager",
            Comment = "Reviewed.",
        };

        var result = await page.OnPostAsync(
            "release-decision",
            CancellationToken.None);

        Assert.IsType<RedirectToPageResult>(result);
        Assert.Contains(
            expectedFeedback,
            Assert.IsType<string>(page.TempData[DecisionModel.FeedbackKey]),
            StringComparison.OrdinalIgnoreCase);
        Assert.Single(service.Submissions);
    }

    [Fact]
    public async Task Unavailable_continuation_redirects_with_safe_feedback()
    {
        var page = CreatePage(new StubDecisionInteractionService(
            DecisionLoadResult.From(DecisionLoadOutcome.TechnicalFailure)));

        var result = await page.OnGetAsync(
            "release-decision",
            CancellationToken.None);

        Assert.IsType<RedirectToPageResult>(result);
        Assert.Contains(
            "could not be restored safely",
            Assert.IsType<string>(page.TempData[DecisionModel.FeedbackKey]),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Reusing_response_id_with_different_input_is_a_conflict_with_one_terminal_effect()
    {
        await using var harness = await WorkflowHarness.CreateAsync();
        var responseId = Guid.NewGuid();
        var first = new DecisionSubmission(
            responseId,
            HumanDecision.Approve,
            "release-manager",
            "Reviewed the package.");
        var conflicting = new DecisionSubmission(
            responseId,
            HumanDecision.Reject,
            "release-manager",
            "Changed decision.");

        Assert.Equal(
            DecisionSubmitOutcome.Succeeded,
            await harness.Interactions.SubmitAsync(harness.ReleaseId, first));
        Assert.Equal(
            DecisionSubmitOutcome.ResponseConflict,
            await harness.Interactions.SubmitAsync(harness.ReleaseId, conflicting));

        var detail = await harness.ReadAsync();
        Assert.NotNull(detail);
        Assert.Equal(ProcessPhase.Approved, detail.Release.Phase);
        Assert.Equal(first.ResponseId, detail.TerminalResponse!.Response.Id);
        Assert.Single(
            detail.Timeline,
            entry => entry.Kind is TimelineEntryKind.HumanResponseAccepted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_or_mismatched_continuation_has_no_human_response(
        bool mismatch)
    {
        await using var harness = await WorkflowHarness.CreateAsync();
        if (mismatch)
        {
            await harness.MismatchCorrelationAsync();
        }
        else
        {
            harness.RemoveCheckpointAndRestart();
        }

        var outcome = await harness.Interactions.SubmitAsync(
            harness.ReleaseId,
            new DecisionSubmission(
                Guid.NewGuid(),
                HumanDecision.Approve,
                "release-manager",
                "Reviewed the package."));

        Assert.Equal(DecisionSubmitOutcome.TechnicalFailure, outcome);
        var detail = await harness.ReadAsync();
        Assert.NotNull(detail);
        Assert.Null(detail.TerminalResponse);
        Assert.DoesNotContain(
            detail.Timeline,
            entry => entry.Kind is TimelineEntryKind.HumanResponseAccepted);
    }

    private static DecisionModel CreatePage(StubDecisionInteractionService service)
    {
        var httpContext = new DefaultHttpContext();
        return new DecisionModel(service)
        {
            PageContext = new PageContext { HttpContext = httpContext },
            TempData = new TempDataDictionary(
                httpContext,
                new TestTempDataProvider()),
        };
    }

    private static ActiveDecisionInteraction ActiveState()
    {
        var releaseId = new ReleaseId("release-decision");
        var sources = Enum.GetValues<ReadinessCheck>()
            .Select(check => new BranchResult(
                Guid.NewGuid(),
                releaseId,
                2,
                check,
                BranchOutcome.Passed,
                ExecutionDisposition.Executed,
                PlanningReason.InitialEvaluation,
                "Executed for the decision package.",
                Guid.NewGuid(),
                (EvidenceKind)(int)check,
                new UtcInstant(Now.Value.AddHours(2)),
                ["Provider attempt 1 succeeded."],
                check is ReadinessCheck.Test
                    ? new Dictionary<string, string> { ["coverage"] = "99%" }
                    : new Dictionary<string, string>(),
                null,
                null))
            .ToArray();
        var snapshot = new DecisionSnapshot(
            Guid.NewGuid(),
            releaseId,
            Guid.NewGuid(),
            2,
            sources,
            new UtcInstant(Now.Value.AddHours(2)),
            "Checkpoint-carried decision brief.",
            Now);
        var request = new HumanDecisionRequest(
            Guid.NewGuid(), releaseId, snapshot.Id, Now);
        return new ActiveDecisionInteraction(
            new ApprovalRequest(snapshot, request),
            "maf-approval-request-42");
    }

    private sealed class StubDecisionInteractionService(
        DecisionLoadResult loadResult,
        DecisionSubmitOutcome submitOutcome = DecisionSubmitOutcome.Succeeded)
        : IDecisionInteractionService
    {
        public List<DecisionSubmission> Submissions { get; } = [];

        public Task<DecisionLoadResult> LoadAsync(
            ReleaseId releaseId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(loadResult);

        public Task<DecisionSubmitOutcome> SubmitAsync(
            ReleaseId releaseId,
            DecisionSubmission submission,
            CancellationToken cancellationToken = default)
        {
            Submissions.Add(submission);
            return Task.FromResult(submitOutcome);
        }
    }

    private sealed class WorkflowHarness : IAsyncDisposable
    {
        private readonly ReadinessWorkflowTestHost _host;
        private CheckpointStoreCoordinator _checkpoints;
        private readonly string _checkpointPath;

        private WorkflowHarness(
            ReadinessWorkflowTestHost host,
            CheckpointStoreCoordinator checkpoints,
            string checkpointPath,
            PendingApprovalWait pending,
            IDecisionInteractionService interactions)
        {
            _host = host;
            _checkpoints = checkpoints;
            _checkpointPath = checkpointPath;
            Pending = pending;
            Interactions = interactions;
        }

        public ReleaseId ReleaseId => _host.Submission.ReleaseId;

        public PendingApprovalWait Pending { get; }

        public IDecisionInteractionService Interactions { get; private set; }

        public static async Task<WorkflowHarness> CreateAsync()
        {
            var host = await ReadinessWorkflowTestHost.CreateForOutcomesAsync(
                BranchOutcome.Passed);
            var checkpointPath = Path.Combine(
                Path.GetTempPath(),
                "decision-page-checkpoints",
                Guid.NewGuid().ToString("N"));
            var checkpoints = new CheckpointStoreCoordinator(
                Directory.CreateDirectory(checkpointPath));
            var timeProvider = new FixedTimeProvider(
                host.Submission.RequestedDeploymentWindow.Start.Value);
            var workflow = new ReleaseWorkflowService(
                host.DataService,
                checkpoints,
                timeProvider);
            var pending = Assert.IsType<PendingApprovalWait>(await workflow.StartAsync(
                host.Submission,
                host.Input,
                $"decision-page-{Guid.NewGuid():N}"));
            var interactions = new DecisionInteractionService(
                host.DataService,
                workflow,
                timeProvider);
            return new WorkflowHarness(
                host,
                checkpoints,
                checkpointPath,
                pending,
                interactions);
        }

        public Task<ReleaseReadinessCoordinator.Data.ReleaseDetailProjection?> ReadAsync() =>
            _host.DataService.GetReleaseDetailAsync(ReleaseId);

        public async Task MismatchCorrelationAsync()
        {
            var detail = await ReadAsync();
            var correlation = detail!.WorkflowCorrelation!;
            await _host.DataService.SaveWorkflowCorrelationAsync(
                new ReleaseReadinessCoordinator.Domain.WorkflowCorrelationRecord(
                    ReleaseId,
                    correlation.WorkflowSessionId,
                    $"mismatched-{Guid.NewGuid():N}",
                    WorkflowRequestKind.Approval,
                    Now),
                $"decision-page:mismatch:{Guid.NewGuid():N}");
        }

        public void RemoveCheckpointAndRestart()
        {
            _checkpoints.Dispose();
            Directory.Delete(_checkpointPath, recursive: true);
            _checkpoints = new CheckpointStoreCoordinator(
                Directory.CreateDirectory(_checkpointPath));
            var workflow = new ReleaseWorkflowService(
                _host.DataService,
                _checkpoints,
                new FixedTimeProvider(
                    _host.Submission.RequestedDeploymentWindow.Start.Value));
            Interactions = new DecisionInteractionService(
                _host.DataService,
                workflow,
                new FixedTimeProvider(
                    _host.Submission.RequestedDeploymentWindow.Start.Value));
        }

        public async ValueTask DisposeAsync()
        {
            _checkpoints.Dispose();
            await _host.DisposeAsync();
            if (Directory.Exists(_checkpointPath))
            {
                Directory.Delete(_checkpointPath, recursive: true);
            }
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class TestTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) =>
            new Dictionary<string, object>();

        public void SaveTempData(
            HttpContext context,
            IDictionary<string, object> values)
        {
        }
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

        public static async Task<RenderedPage> StartAsync(
            IDecisionInteractionService interactionService)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(DecisionPageTests).Assembly
                    .GetReferencedAssemblies()
                    .Single(name => name.Name == "ReleaseReadinessCoordinator")
                    .FullName,
                EnvironmentName = "Development",
            });
            builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Services.AddRazorPages();
            builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
            builder.Services.AddSingleton(interactionService);

            var application = builder.Build();
            application.UseDeveloperExceptionPage();
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
}
