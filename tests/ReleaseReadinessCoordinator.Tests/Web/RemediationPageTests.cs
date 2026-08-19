using System.Collections.Immutable;
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
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReleaseReadinessCoordinator.Data;
using ReleaseReadinessCoordinator.Domain;
using ReleaseReadinessCoordinator.Pages.Releases;
using ReleaseReadinessCoordinator.Workflow;

namespace ReleaseReadinessCoordinator.Tests.Web;

public sealed class RemediationPageTests
{
    private static readonly UtcInstant Now =
        new(new DateTimeOffset(2026, 8, 19, 9, 0, 0, TimeSpan.Zero));

    [Fact]
    public async Task Active_request_renders_all_problems_and_only_remediation_inputs_with_correlation_token()
    {
        var state = ActiveState();
        var service = new StubRemediationInteractionService(state);
        await using var page = await RenderedPage.StartAsync(service);

        var response = await page.Client.GetAsync("/Releases/release-remediation/Remediate");
        var html = await response.Content.ReadAsStringAsync();

        Assert.True(
            response.StatusCode == HttpStatusCode.OK,
            $"Expected an OK response but received {response.StatusCode}.\n{html}");
        Assert.Contains("Remediate release release-remediation", html, StringComparison.Ordinal);
        Assert.Contains("payments-api", html, StringComparison.Ordinal);
        Assert.Contains("2026.08.19", html, StringComparison.Ordinal);
        Assert.Contains("Test evidence is missing", html, StringComparison.Ordinal);
        Assert.Contains("Security evidence is missing", html, StringComparison.Ordinal);
        Assert.Contains("workflow-request-42", html, StringComparison.Ordinal);
        Assert.Contains("name=\"Input.CorrelationToken\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"Input.ReplaceTestEvidence\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"Input.ReplaceSecurityEvidence\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"Input.ReplaceChangeEvidence\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"Input.TestRunVersion\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"Input.TestPassRatePercent\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"Input.SecurityScanVersion\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"Input.SecurityExceptions\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"Input.ChangeWindowStart\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"Input.RerunTest\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"Input.RerunSecurity\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"Input.RerunChange\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"Input.ReleaseId\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"Input.ServiceName\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"Input.ReleaseVersion\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"Input.RequestId\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"Input.SubmissionId\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"__RequestVerificationToken\"", html, StringComparison.Ordinal);
        Assert.Contains("<form method=\"post\"", html, StringComparison.Ordinal);
        Assert.Contains("<fieldset", html, StringComparison.Ordinal);
        Assert.Contains("<legend", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Valid_submission_derives_new_evidence_versions_and_redirects_after_continuation()
    {
        var state = ActiveState(includeCurrentEvidence: true);
        var service = new StubRemediationInteractionService(state);
        var page = CreatePage(service);
        page.Input = new RemediateModel.InputModel
        {
            CorrelationToken = state.CorrelationToken,
            ReplaceTestEvidence = true,
            TestRunVersion = "2026.08.19",
            TestCompletedAt = Now.Value,
            TestPassRatePercent = 99m,
            CriticalSuiteFailures = string.Empty,
            ReplaceSecurityEvidence = true,
            SecurityScanVersion = "2026.08.19",
            SecurityScannedAt = Now.Value,
            CriticalFindingIds = string.Empty,
            HighFindingIds = "HIGH-7",
            SecurityExceptions = "HIGH-7|payments-api|2026-08-20T10:00:00Z",
            ReplaceChangeEvidence = true,
            ChangeApproved = true,
            ChangeWindowStart = Now.Value,
            ChangeWindowEnd = Now.Value.AddHours(2),
            RerunSecurity = true,
        };

        var result = await page.OnPostAsync("release-remediation", CancellationToken.None);

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("/Releases/Detail", redirect.PageName);
        Assert.Equal("release-remediation", redirect.RouteValues!["releaseId"]);
        Assert.Contains("continued", Assert.IsType<string>(page.TempData["WorkflowFeedback"]));
        var submission = Assert.Single(service.Submissions);
        Assert.Equal(state.CorrelationToken, submission.CorrelationToken);
        Assert.Equal([ReadinessCheck.Security], submission.SelectedChecks);
        Assert.Collection(
            submission.Evidence,
            test =>
            {
                var replacement = Assert.IsType<TestEvidenceRecord>(test);
                Assert.Equal(4, replacement.Version);
                Assert.Equal(state.CurrentEvidence[EvidenceKind.Test].Id, replacement.SupersedesEvidenceId);
                Assert.Equal(0.99m, replacement.PassRate);
                Assert.Empty(replacement.CriticalSuiteFailures!.Value);
            },
            security =>
            {
                var replacement = Assert.IsType<SecurityEvidenceRecord>(security);
                Assert.Equal(1, replacement.Version);
                Assert.Null(replacement.SupersedesEvidenceId);
                Assert.Equal(
                    "HIGH-7",
                    Assert.Single(replacement.UnresolvedHighFindingIds!.Value));
                Assert.Equal("payments-api", replacement.ApprovedExceptions!["HIGH-7"].Scope);
            },
            change =>
            {
                var replacement = Assert.IsType<ChangeEvidenceRecord>(change);
                Assert.Equal(3, replacement.Version);
                Assert.Equal(state.CurrentEvidence[EvidenceKind.Change].Id, replacement.SupersedesEvidenceId);
                Assert.True(replacement.IsApproved);
            });
    }

    [Fact]
    public async Task Invalid_model_state_rerenders_active_request_without_submitting()
    {
        var state = ActiveState();
        var service = new StubRemediationInteractionService(state);
        var page = CreatePage(service);
        page.Input.CorrelationToken = state.CorrelationToken;
        page.ModelState.AddModelError("Input.TestPassRatePercent", "Invalid pass rate.");

        var result = await page.OnPostAsync("release-remediation", CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.Same(state, page.State);
        Assert.Empty(service.Submissions);
    }

    [Theory]
    [InlineData(RemediationSubmitOutcome.CorrelationMismatch, "no changes were applied")]
    [InlineData(RemediationSubmitOutcome.NoLongerActive, "no longer active")]
    public async Task Stale_or_mismatched_submission_redirects_with_safe_feedback(
        RemediationSubmitOutcome outcome,
        string expectedFeedback)
    {
        var state = ActiveState();
        var service = new StubRemediationInteractionService(state, outcome);
        var page = CreatePage(service);
        page.Input.CorrelationToken = "posted-correlation";

        var result = await page.OnPostAsync("release-remediation", CancellationToken.None);

        Assert.IsType<RedirectToPageResult>(result);
        Assert.Contains(
            expectedFeedback,
            Assert.IsType<string>(page.TempData["WorkflowFeedback"]),
            StringComparison.OrdinalIgnoreCase);
        Assert.Single(service.Submissions);
        Assert.Equal("posted-correlation", service.Submissions[0].CorrelationToken);
    }

    [Fact]
    public async Task Inactive_request_does_not_render()
    {
        var page = CreatePage(new StubRemediationInteractionService(active: null));

        var result = await page.OnGetAsync("release-remediation", CancellationToken.None);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task Mismatch_and_double_submit_have_one_selective_rerun_effect()
    {
        await using var harness = await WorkflowHarness.CreateAsync();
        var initialEvidence = await harness.StartBlockedReleaseAsync();
        var active = await harness.Interactions.GetActiveAsync(harness.ReleaseId);
        Assert.NotNull(active);
        var currentTest = initialEvidence.Single(item => item.Kind is EvidenceKind.Test);
        EvidenceRecord replacement = new TestEvidenceRecord(
            Guid.NewGuid(), harness.ReleaseId, 2, Now, currentTest.Id,
            "2026.08.19", Now, 0.99m, []);

        var mismatch = await harness.Interactions.SubmitAsync(
            harness.ReleaseId,
            "wrong-correlation",
            [replacement],
            [ReadinessCheck.Security]);

        Assert.Equal(RemediationSubmitOutcome.CorrelationMismatch, mismatch);
        Assert.Empty((await harness.ReadAsync())!.RemediationSubmissions);

        var first = await harness.Interactions.SubmitAsync(
            harness.ReleaseId,
            active!.CorrelationToken,
            [replacement],
            [ReadinessCheck.Security]);
        var duplicate = await harness.Interactions.SubmitAsync(
            harness.ReleaseId,
            active.CorrelationToken,
            [replacement],
            [ReadinessCheck.Security]);

        Assert.Equal(RemediationSubmitOutcome.Succeeded, first);
        Assert.Equal(RemediationSubmitOutcome.NoLongerActive, duplicate);
        var detail = await harness.ReadAsync();
        Assert.NotNull(detail);
        Assert.Single(detail.RemediationSubmissions);
        Assert.Equal(2, detail.EvaluationRounds.Length);
        Assert.Collection(
            detail.EvaluationRounds[1].Results,
            test =>
            {
                Assert.Equal(ExecutionDisposition.Executed, test.Disposition);
                Assert.Equal(PlanningReason.EvidenceChanged, test.PlanningReason);
            },
            security =>
            {
                Assert.Equal(ExecutionDisposition.Executed, security.Disposition);
                Assert.Equal(PlanningReason.ExplicitlySelected, security.PlanningReason);
            },
            change =>
            {
                Assert.Equal(ExecutionDisposition.Reused, change.Disposition);
                Assert.Equal(PlanningReason.StillCurrent, change.PlanningReason);
            });
    }

    private static RemediateModel CreatePage(StubRemediationInteractionService service)
    {
        var httpContext = new DefaultHttpContext();
        return new RemediateModel(service, new FixedTimeProvider(Now.Value))
        {
            PageContext = new PageContext { HttpContext = httpContext },
            TempData = new TempDataDictionary(httpContext, new TestTempDataProvider()),
        };
    }

    private static ActiveRemediationInteraction ActiveState(bool includeCurrentEvidence = false)
    {
        var releaseId = new ReleaseId("release-remediation");
        var submission = new ReleaseSubmission(
            releaseId,
            "payments-api",
            "2026.08.19",
            new UtcInterval(Now, new UtcInstant(Now.Value.AddHours(1))),
            Now);
        var release = Release.Create(submission)
            .TransitionTo(ProcessPhase.WaitingForRemediation, Now);
        var problems = new[]
        {
            Problem(releaseId, ReadinessCheck.Test, "Test evidence is missing"),
            Problem(releaseId, ReadinessCheck.Security, "Security evidence is missing"),
        };
        var request = new RemediationRequest(
            Guid.NewGuid(), releaseId, 1, Now, problems);
        IReadOnlyDictionary<EvidenceKind, EvidenceRecord> evidence = includeCurrentEvidence
            ? new Dictionary<EvidenceKind, EvidenceRecord>
            {
                [EvidenceKind.Test] = new TestEvidenceRecord(
                    Guid.NewGuid(), releaseId, 3, Now, Guid.NewGuid(),
                    "2026.08.18", Now, 0.90m, []),
                [EvidenceKind.Change] = new ChangeEvidenceRecord(
                    Guid.NewGuid(), releaseId, 2, Now, Guid.NewGuid(),
                    false, null),
            }
            : ImmutableDictionary<EvidenceKind, EvidenceRecord>.Empty;
        return new ActiveRemediationInteraction(
            release,
            request,
            evidence,
            "workflow-request-42");
    }

    private static BranchResult Problem(
        ReleaseId releaseId,
        ReadinessCheck check,
        string finding) =>
        new(
            Guid.NewGuid(),
            releaseId,
            1,
            check,
            BranchOutcome.MissingEvidence,
            ExecutionDisposition.Executed,
            PlanningReason.InitialEvaluation,
            "Executed because this was the initial evaluation.",
            null,
            (EvidenceKind)(int)check,
            null,
            ["No evidence record was available."],
            new Dictionary<string, string> { ["problem"] = finding },
            null,
            null);

    private sealed class StubRemediationInteractionService(
        ActiveRemediationInteraction? active,
        RemediationSubmitOutcome outcome = RemediationSubmitOutcome.Succeeded)
        : IRemediationInteractionService
    {
        public List<SubmittedRemediation> Submissions { get; } = [];

        public Task<ActiveRemediationInteraction?> GetActiveAsync(
            ReleaseId releaseId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(active?.Release.Submission.ReleaseId == releaseId ? active : null);

        public Task<RemediationSubmitOutcome> SubmitAsync(
            ReleaseId releaseId,
            string correlationToken,
            IReadOnlyCollection<EvidenceRecord> evidenceReplacements,
            IReadOnlyCollection<ReadinessCheck> explicitlySelectedChecks,
            CancellationToken cancellationToken = default)
        {
            Submissions.Add(new SubmittedRemediation(
                releaseId,
                correlationToken,
                [.. evidenceReplacements],
                [.. explicitlySelectedChecks]));
            return Task.FromResult(outcome);
        }
    }

    private sealed record SubmittedRemediation(
        ReleaseId ReleaseId,
        string CorrelationToken,
        EvidenceRecord[] Evidence,
        ReadinessCheck[] SelectedChecks);

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

    private sealed class WorkflowHarness : IAsyncDisposable
    {
        private readonly string _databasePath;
        private readonly string _checkpointPath;
        private readonly AppDbContext _context;
        private readonly CheckpointStoreCoordinator _checkpoints;

        private WorkflowHarness(
            string databasePath,
            string checkpointPath,
            AppDbContext context,
            CheckpointStoreCoordinator checkpoints,
            ApplicationDataService dataService)
        {
            _databasePath = databasePath;
            _checkpointPath = checkpointPath;
            _context = context;
            _checkpoints = checkpoints;
            ReleaseId = new ReleaseId("remediation-selective-rerun");
            var timeProvider = new FixedTimeProvider(Now.Value);
            var workflow = new ReleaseWorkflowService(dataService, checkpoints, timeProvider);
            Submissions = new ReleaseSubmissionApplicationService(
                dataService,
                workflow,
                timeProvider);
            Interactions = new RemediationInteractionService(dataService, workflow, timeProvider);
            Data = dataService;
        }

        public ReleaseId ReleaseId { get; }

        public ReleaseSubmissionApplicationService Submissions { get; }

        public IRemediationInteractionService Interactions { get; }

        private IApplicationDataService Data { get; }

        public static async Task<WorkflowHarness> CreateAsync()
        {
            var databasePath = Path.Combine(
                Path.GetTempPath(),
                $"remediation-page-{Guid.NewGuid():N}.db");
            var checkpointPath = Path.Combine(
                Path.GetTempPath(),
                "release-readiness-tests",
                Guid.NewGuid().ToString("N"));
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={databasePath};Pooling=False")
                .Options;
            var context = new AppDbContext(options);
            await context.Database.EnsureCreatedAsync();
            var checkpoints = new CheckpointStoreCoordinator(
                Directory.CreateDirectory(checkpointPath));
            return new WorkflowHarness(
                databasePath,
                checkpointPath,
                context,
                checkpoints,
                new ApplicationDataService(context));
        }

        public async Task<EvidenceRecord[]> StartBlockedReleaseAsync()
        {
            var submission = new ReleaseSubmission(
                ReleaseId,
                "payments-api",
                "2026.08.19",
                new UtcInterval(Now, new UtcInstant(Now.Value.AddHours(1))),
                Now);
            EvidenceRecord[] evidence =
            [
                new TestEvidenceRecord(
                    Guid.NewGuid(), ReleaseId, 1, Now, null,
                    submission.ReleaseVersion, Now, 0.90m, []),
                new SecurityEvidenceRecord(
                    Guid.NewGuid(), ReleaseId, 1, Now, null,
                    submission.ReleaseVersion, Now, [], [],
                    new Dictionary<string, (string Scope, UtcInstant ExpiresAt)>()),
                new ChangeEvidenceRecord(
                    Guid.NewGuid(), ReleaseId, 1, Now, null,
                    true,
                    new UtcInterval(
                        new UtcInstant(Now.Value.AddHours(-1)),
                        new UtcInstant(Now.Value.AddHours(2)))),
            ];

            await Submissions.SubmitAsync(submission, evidence);
            return evidence;
        }

        public Task<ReleaseDetailProjection?> ReadAsync() =>
            Data.GetReleaseDetailAsync(ReleaseId);

        public async ValueTask DisposeAsync()
        {
            _checkpoints.Dispose();
            await _context.DisposeAsync();
            File.Delete(_databasePath);
            if (Directory.Exists(_checkpointPath))
            {
                Directory.Delete(_checkpointPath, recursive: true);
            }
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
            IRemediationInteractionService interactionService)
        {
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(RemediationPageTests).Assembly
                    .GetReferencedAssemblies()
                    .Single(name => name.Name == "ReleaseReadinessCoordinator")
                    .FullName,
                EnvironmentName = "Development",
            });
            builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Services.AddRazorPages();
            builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
            builder.Services.AddSingleton(TimeProvider.System);
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
