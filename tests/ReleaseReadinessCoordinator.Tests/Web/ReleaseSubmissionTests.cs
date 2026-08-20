using System.Net;
using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReleaseReadinessCoordinator.Data;
using ReleaseReadinessCoordinator.Domain;
using ReleaseReadinessCoordinator.Pages.Releases;
using ReleaseReadinessCoordinator.Simulation;
using ReleaseReadinessCoordinator.Workflow;

namespace ReleaseReadinessCoordinator.Tests.Web;

public sealed class ReleaseSubmissionTests
{
    private static readonly DateTimeOffset SubmittedAt =
        new(2026, 8, 16, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Initial_evidence_sections_explain_availability_and_mark_required_fields()
    {
        await using var page = await RenderedPage.StartAsync();

        var unavailableResponse = await page.Client.GetAsync("/");
        var unavailableHtml = await unavailableResponse.Content.ReadAsStringAsync();
        var availableResponse = await page.Client.GetAsync("/?fixture=complete");
        var availableHtml = await availableResponse.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, unavailableResponse.StatusCode);
        Assert.Equal(HttpStatusCode.OK, availableResponse.StatusCode);
        Assert.Equal(3, Count(unavailableHtml, "Evidence available now"));
        Assert.DoesNotContain("Supply initial", unavailableHtml, StringComparison.Ordinal);
        Assert.Contains("data-evidence-availability", unavailableHtml, StringComparison.Ordinal);
        Assert.Contains("id=\"test-evidence-fields\"", unavailableHtml, StringComparison.Ordinal);
        Assert.Contains("id=\"security-evidence-fields\"", unavailableHtml, StringComparison.Ordinal);
        Assert.Contains("id=\"change-evidence-fields\"", unavailableHtml, StringComparison.Ordinal);
        Assert.Contains(
            "id=\"change-evidence-fields\" class=\"row g-3 align-items-end\"",
            unavailableHtml,
            StringComparison.Ordinal);
        Assert.DoesNotContain("col-md-4 form-check ms-2 mt-5", unavailableHtml, StringComparison.Ordinal);
        Assert.Contains("fields.hidden = !available", unavailableHtml, StringComparison.Ordinal);
        Assert.Contains("input.disabled = !available", unavailableHtml, StringComparison.Ordinal);

        string[] requiredInputs =
        [
            "Input.TestRunVersion",
            "Input.TestCompletedAt",
            "Input.TestPassRatePercent",
            "Input.SecurityScanVersion",
            "Input.SecurityScannedAt",
            "Input.ChangeWindowStart",
            "Input.ChangeWindowEnd",
        ];
        foreach (var requiredInput in requiredInputs)
        {
            Assert.Contains("disabled", InputTag(unavailableHtml, requiredInput), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("required", InputTag(availableHtml, requiredInput), StringComparison.OrdinalIgnoreCase);
            Assert.Contains("aria-required=\"true\"", InputTag(availableHtml, requiredInput), StringComparison.OrdinalIgnoreCase);
        }

        Assert.Contains("Required when evidence is available", availableHtml, StringComparison.Ordinal);

        foreach (var (name, instant) in new[]
                 {
                     ("Input.DeploymentWindowStart", DemoReleaseFixtures.Complete.DeploymentWindowStart),
                     ("Input.DeploymentWindowEnd", DemoReleaseFixtures.Complete.DeploymentWindowEnd),
                     ("Input.TestCompletedAt", DemoReleaseFixtures.Complete.TestCompletedAt!.Value),
                     ("Input.SecurityScannedAt", DemoReleaseFixtures.Complete.SecurityScannedAt!.Value),
                     ("Input.ChangeWindowStart", DemoReleaseFixtures.Complete.ChangeWindowStart!.Value),
                     ("Input.ChangeWindowEnd", DemoReleaseFixtures.Complete.ChangeWindowEnd!.Value),
                 })
        {
            var tag = InputTag(availableHtml, name);
            Assert.Contains("type=\"datetime-local\"", tag, StringComparison.OrdinalIgnoreCase);
            Assert.Contains($"value=\"{LocalInput(instant)}\"", tag, StringComparison.Ordinal);
            Assert.DoesNotContain("+00:00", tag, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Complete_demo_fixture_persists_release_evidence_starts_workflow_and_redirects()
    {
        await using var harness = await SubmissionHarness.CreateAsync();
        var page = harness.CreatePage();
        page.Input = NewModel.InputModel.FromFixture(DemoReleaseFixtures.Complete);

        var result = await page.OnPostAsync(CancellationToken.None);

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("/Releases/Detail", redirect.PageName);
        Assert.Equal(DemoReleaseFixtures.Complete.ReleaseId, redirect.RouteValues!["releaseId"]);
        Assert.DoesNotContain("revision", redirect.RouteValues.Keys);

        var detail = await harness.ReadAsync(DemoReleaseFixtures.Complete.ReleaseId);
        Assert.NotNull(detail);
        Assert.Equal(DemoReleaseFixtures.Complete.ServiceName, detail.Release.Submission.ServiceName);
        Assert.Equal(DemoReleaseFixtures.Complete.ReleaseVersion, detail.Release.Submission.ReleaseVersion);
        Assert.Equal(SubmittedAt, detail.Release.Submission.SubmittedAt.Value);
        Assert.Equal(
            DemoReleaseFixtures.Complete.DeploymentWindowStart,
            detail.Release.Submission.RequestedDeploymentWindow.Start.Value);
        Assert.Equal(
            DemoReleaseFixtures.Complete.DeploymentWindowEnd,
            detail.Release.Submission.RequestedDeploymentWindow.End.Value);
        Assert.Equal(3, detail.CurrentEvidence.Count);
        var test = Assert.IsType<TestEvidenceRecord>(detail.CurrentEvidence[EvidenceKind.Test]);
        Assert.Equal(DemoReleaseFixtures.Complete.TestRunVersion, test.TestRunVersion);
        Assert.Equal(DemoReleaseFixtures.Complete.TestCompletedAt, test.CompletedAt?.Value);
        Assert.Equal(DemoReleaseFixtures.Complete.TestPassRatePercent / 100m, test.PassRate);
        Assert.Empty(test.CriticalSuiteFailures!.Value);
        var security = Assert.IsType<SecurityEvidenceRecord>(detail.CurrentEvidence[EvidenceKind.Security]);
        Assert.Equal(DemoReleaseFixtures.Complete.SecurityScanVersion, security.ScanVersion);
        Assert.Equal(DemoReleaseFixtures.Complete.SecurityScannedAt, security.ScannedAt?.Value);
        Assert.Empty(security.UnresolvedCriticalFindingIds!.Value);
        Assert.Empty(security.UnresolvedHighFindingIds!.Value);
        var change = Assert.IsType<ChangeEvidenceRecord>(detail.CurrentEvidence[EvidenceKind.Change]);
        Assert.Equal(DemoReleaseFixtures.Complete.ChangeApproved, change.IsApproved);
        Assert.Equal(DemoReleaseFixtures.Complete.ChangeWindowStart, change.ApprovedWindow?.Start.Value);
        Assert.Equal(DemoReleaseFixtures.Complete.ChangeWindowEnd, change.ApprovedWindow?.End.Value);
        Assert.NotNull(detail.WorkflowCorrelation);
        Assert.Equal(WorkflowRequestKind.Approval, detail.WorkflowCorrelation.PendingRequestKind);
        var round = Assert.Single(detail.EvaluationRounds);
        Assert.Equal(3, round.Results.Length);
        Assert.All(round.Results, result => Assert.Equal(ExecutionDisposition.Executed, result.Disposition));
        Assert.Equal(ProcessPhase.WaitingForApproval, detail.Release.Phase);
        Assert.Single(detail.DecisionSnapshots);
        Assert.Single(detail.HumanDecisionRequests);
        Assert.Equal(3, detail.Timeline.Length);
        Assert.Equal(TimelineEntryKind.ReleaseSubmitted, detail.Timeline[0].Kind);
        Assert.Equal(TimelineEntryKind.EvaluationCompleted, detail.Timeline[1].Kind);
        Assert.Equal(TimelineEntryKind.ApprovalRequested, detail.Timeline[2].Kind);
    }

    [Fact]
    public async Task Submit_release_ignores_validation_from_the_continue_workflow_form()
    {
        await using var harness = await SubmissionHarness.CreateAsync();
        var page = harness.CreatePage();
        page.Input = NewModel.InputModel.FromFixture(DemoReleaseFixtures.Complete);
        page.ModelState.AddModelError(
            nameof(NewModel.ExistingReleaseId),
            "The Release ID field is required.");

        var result = await page.OnPostAsync(CancellationToken.None);

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("/Releases/Detail", redirect.PageName);
        Assert.NotNull(await harness.ReadAsync(DemoReleaseFixtures.Complete.ReleaseId));
    }

    [Fact]
    public async Task Omitted_evidence_is_persisted_as_no_current_record_for_each_missing_branch()
    {
        await using var harness = await SubmissionHarness.CreateAsync();
        var page = harness.CreatePage();
        page.Input = NewModel.InputModel.FromFixture(DemoReleaseFixtures.MissingEvidence);

        var result = await page.OnPostAsync(CancellationToken.None);

        Assert.IsType<RedirectToPageResult>(result);
        var detail = await harness.ReadAsync(DemoReleaseFixtures.MissingEvidence.ReleaseId);
        Assert.NotNull(detail);
        Assert.Empty(detail.CurrentEvidence);
        Assert.NotNull(detail.WorkflowCorrelation);
    }

    [Fact]
    public async Task Available_initial_evidence_requires_essential_fields_before_submission()
    {
        await using var harness = await SubmissionHarness.CreateAsync();
        var page = harness.CreatePage();
        page.Input = NewModel.InputModel.FromFixture(DemoReleaseFixtures.MissingEvidence) with
        {
            IncludeTestEvidence = true,
            IncludeSecurityEvidence = true,
            IncludeChangeEvidence = true,
        };

        var result = await page.OnPostAsync(CancellationToken.None);

        Assert.IsType<PageResult>(result);
        string[] requiredKeys =
        [
            "Input.TestRunVersion",
            "Input.TestCompletedAt",
            "Input.TestPassRatePercent",
            "Input.SecurityScanVersion",
            "Input.SecurityScannedAt",
            "Input.ChangeWindowStart",
            "Input.ChangeWindowEnd",
        ];
        Assert.All(
            requiredKeys,
            key => Assert.True(page.ModelState.ContainsKey(key), $"Missing validation for {key}."));
        Assert.Null(await harness.ReadAsync(DemoReleaseFixtures.MissingEvidence.ReleaseId));
    }

    [Fact]
    public async Task Duplicate_release_id_returns_conflict_page_and_does_not_reopen_or_replace_release()
    {
        await using var harness = await SubmissionHarness.CreateAsync();
        var original = harness.CreatePage();
        original.Input = NewModel.InputModel.FromFixture(DemoReleaseFixtures.Complete);
        Assert.IsType<RedirectToPageResult>(await original.OnPostAsync(CancellationToken.None));

        var duplicate = harness.CreatePage();
        duplicate.Input = NewModel.InputModel.FromFixture(DemoReleaseFixtures.Complete) with
        {
            ServiceName = "replacement-service",
        };

        var result = await duplicate.OnPostAsync(CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.Equal(StatusCodes.Status409Conflict, duplicate.Response.StatusCode);
        Assert.Contains(
            duplicate.ModelState[string.Empty]!.Errors,
            error => error.ErrorMessage.Contains("already exists", StringComparison.OrdinalIgnoreCase));
        var detail = await harness.ReadAsync(DemoReleaseFixtures.Complete.ReleaseId);
        Assert.NotNull(detail);
        Assert.Equal(DemoReleaseFixtures.Complete.ServiceName, detail.Release.Submission.ServiceName);
        Assert.Equal(ProcessPhase.WaitingForApproval, detail.Release.Phase);
    }

    [Theory]
    [InlineData("complete")]
    [InlineData("missing")]
    public async Task Continue_existing_workflow_opens_release_details(
        string fixtureName)
    {
        var fixture = fixtureName == "complete"
            ? DemoReleaseFixtures.Complete
            : DemoReleaseFixtures.MissingEvidence;
        await using var harness = await SubmissionHarness.CreateAsync();
        var submission = harness.CreatePage();
        submission.Input = NewModel.InputModel.FromFixture(fixture);
        Assert.IsType<RedirectToPageResult>(
            await submission.OnPostAsync(CancellationToken.None));
        var page = harness.CreatePage();
        page.ExistingReleaseId = fixture.ReleaseId;

        var result = await page.OnGetAsync(fixture: null, CancellationToken.None);

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("/Releases/Detail", redirect.PageName);
        Assert.Equal(fixture.ReleaseId, redirect.RouteValues!["releaseId"]);
    }

    [Fact]
    public async Task Continue_unknown_release_rerenders_with_safe_feedback()
    {
        await using var harness = await SubmissionHarness.CreateAsync();
        var page = harness.CreatePage();
        page.ExistingReleaseId = "unknown-release";

        var result = await page.OnGetAsync(fixture: null, CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.Contains(
            page.ModelState[nameof(NewModel.ExistingReleaseId)]!.Errors,
            error => error.ErrorMessage.Contains("not found", StringComparison.OrdinalIgnoreCase));
    }

    private sealed class SubmissionHarness : IAsyncDisposable
    {
        private readonly string _databasePath;
        private readonly string _checkpointPath;
        private readonly AppDbContext _context;
        private readonly CheckpointStoreCoordinator _checkpointCoordinator;

        private SubmissionHarness(
            string databasePath,
            string checkpointPath,
            AppDbContext context,
            CheckpointStoreCoordinator checkpointCoordinator)
        {
            _databasePath = databasePath;
            _checkpointPath = checkpointPath;
            _context = context;
            _checkpointCoordinator = checkpointCoordinator;
        }

        public static async Task<SubmissionHarness> CreateAsync()
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"release-submission-{Guid.NewGuid():N}.db");
            var checkpointPath = Path.Combine(
                Path.GetTempPath(), "release-readiness-tests", Guid.NewGuid().ToString("N"));
            var options = new DbContextOptionsBuilder<AppDbContext>()
                .UseSqlite($"Data Source={databasePath};Pooling=False")
                .Options;
            var context = new AppDbContext(options);
            await context.Database.EnsureCreatedAsync();
            var checkpointCoordinator = new CheckpointStoreCoordinator(
                Directory.CreateDirectory(checkpointPath));
            return new SubmissionHarness(databasePath, checkpointPath, context, checkpointCoordinator);
        }

        public NewModel CreatePage()
        {
            var dataService = new ApplicationDataService(_context);
            var timeProvider = new FixedTimeProvider(SubmittedAt);
            var submissionService = new ReleaseSubmissionApplicationService(
                dataService,
                new ReleaseWorkflowService(dataService, _checkpointCoordinator, timeProvider),
                timeProvider);
            var page = new NewModel(submissionService, dataService, timeProvider)
            {
                PageContext = new PageContext
                {
                    HttpContext = new DefaultHttpContext(),
                },
            };
            return page;
        }

        public Task<ReleaseDetailProjection?> ReadAsync(string releaseId) =>
            new ApplicationDataService(_context).GetReleaseDetailAsync(
                new ReleaseId(releaseId));

        public async ValueTask DisposeAsync()
        {
            _checkpointCoordinator.Dispose();
            await _context.DisposeAsync();
            File.Delete(_databasePath);
            if (Directory.Exists(_checkpointPath))
            {
                Directory.Delete(_checkpointPath, recursive: true);
            }
        }
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static int Count(string text, string value) =>
        Regex.Matches(text, Regex.Escape(value), RegexOptions.IgnoreCase).Count;

    private static string LocalInput(DateTimeOffset instant) =>
        TimeZoneInfo.ConvertTime(instant, TimeZoneInfo.Local)
            .ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture);

    private static string InputTag(string html, string name)
    {
        var match = Regex.Match(
            html,
            $"<(?:input|textarea)[^>]*name=\"{Regex.Escape(name)}\"[^>]*>",
            RegexOptions.IgnoreCase);
        Assert.True(match.Success, $"Input '{name}' was not rendered.\n{html}");
        return match.Value;
    }

    private sealed class RenderedPage : IAsyncDisposable
    {
        private readonly WebApplication _application;
        private readonly string _databasePath;
        private readonly string _checkpointPath;

        private RenderedPage(
            WebApplication application,
            HttpClient client,
            string databasePath,
            string checkpointPath)
        {
            _application = application;
            _databasePath = databasePath;
            _checkpointPath = checkpointPath;
            Client = client;
        }

        public HttpClient Client { get; }

        public static async Task<RenderedPage> StartAsync()
        {
            var databasePath = Path.Combine(Path.GetTempPath(), $"release-form-{Guid.NewGuid():N}.db");
            var checkpointPath = Path.Combine(
                Path.GetTempPath(), "release-readiness-tests", Guid.NewGuid().ToString("N"));
            var builder = WebApplication.CreateBuilder(new WebApplicationOptions
            {
                ApplicationName = typeof(ReleaseSubmissionTests).Assembly
                    .GetReferencedAssemblies()
                    .Single(name => name.Name == "ReleaseReadinessCoordinator")
                    .FullName,
                EnvironmentName = "Development",
            });
            builder.WebHost.UseKestrel().UseUrls("http://127.0.0.1:0");
            builder.Logging.ClearProviders();
            builder.Services.AddRazorPages();
            builder.Services.AddDataProtection().UseEphemeralDataProtectionProvider();
            builder.Services.AddSingleton<TimeProvider>(new FixedTimeProvider(SubmittedAt));
            builder.Services.AddDbContext<AppDbContext>(options =>
                options.UseSqlite($"Data Source={databasePath};Pooling=False"));
            builder.Services.AddScoped<IApplicationDataService, ApplicationDataService>();
            builder.Services.AddSingleton(_ => new CheckpointStoreCoordinator(
                Directory.CreateDirectory(checkpointPath)));
            builder.Services.AddScoped(serviceProvider => new ReleaseWorkflowService(
                serviceProvider.GetRequiredService<IApplicationDataService>(),
                serviceProvider.GetRequiredService<CheckpointStoreCoordinator>(),
                serviceProvider.GetRequiredService<TimeProvider>()));
            builder.Services.AddScoped(serviceProvider => new ReleaseSubmissionApplicationService(
                serviceProvider.GetRequiredService<IApplicationDataService>(),
                serviceProvider.GetRequiredService<ReleaseWorkflowService>(),
                serviceProvider.GetRequiredService<TimeProvider>()));

            var application = builder.Build();
            application.UseDeveloperExceptionPage();
            application.MapRazorPages();
            await application.StartAsync();
            var address = application.Services.GetRequiredService<IServer>()
                .Features.Get<IServerAddressesFeature>()!
                .Addresses.Single();
            return new RenderedPage(
                application,
                new HttpClient { BaseAddress = new Uri(address) },
                databasePath,
                checkpointPath);
        }

        public async ValueTask DisposeAsync()
        {
            Client.Dispose();
            await _application.DisposeAsync();
            File.Delete(_databasePath);
            if (Directory.Exists(_checkpointPath))
            {
                Directory.Delete(_checkpointPath, recursive: true);
            }
        }
    }
}
