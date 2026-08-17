using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
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
    public async Task Complete_demo_fixture_persists_release_evidence_starts_workflow_and_redirects()
    {
        await using var harness = await SubmissionHarness.CreateAsync();
        var page = harness.CreatePage();
        page.Input = NewModel.InputModel.FromFixture(DemoReleaseFixtures.Complete);

        var result = await page.OnPostAsync(CancellationToken.None);

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal("/Releases/Detail", redirect.PageName);
        Assert.Equal(DemoReleaseFixtures.Complete.ReleaseId, redirect.RouteValues!["releaseId"]);
        Assert.Equal(DemoReleaseFixtures.Complete.Revision, redirect.RouteValues["revision"]);

        var detail = await harness.ReadAsync(
            DemoReleaseFixtures.Complete.ReleaseId,
            DemoReleaseFixtures.Complete.Revision);
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
        Assert.Single(detail.Timeline);
        Assert.Equal(TimelineEntryKind.ReleaseSubmitted, detail.Timeline[0].Kind);
    }

    [Fact]
    public async Task Omitted_evidence_is_persisted_as_no_current_record_for_each_missing_branch()
    {
        await using var harness = await SubmissionHarness.CreateAsync();
        var page = harness.CreatePage();
        page.Input = NewModel.InputModel.FromFixture(DemoReleaseFixtures.MissingEvidence);

        var result = await page.OnPostAsync(CancellationToken.None);

        Assert.IsType<RedirectToPageResult>(result);
        var detail = await harness.ReadAsync(
            DemoReleaseFixtures.MissingEvidence.ReleaseId,
            DemoReleaseFixtures.MissingEvidence.Revision);
        Assert.NotNull(detail);
        Assert.Empty(detail.CurrentEvidence);
        Assert.NotNull(detail.WorkflowCorrelation);
    }

    [Fact]
    public async Task Duplicate_revision_returns_conflict_page_and_does_not_reopen_or_replace_release()
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
        var detail = await harness.ReadAsync(
            DemoReleaseFixtures.Complete.ReleaseId,
            DemoReleaseFixtures.Complete.Revision);
        Assert.NotNull(detail);
        Assert.Equal(DemoReleaseFixtures.Complete.ServiceName, detail.Release.Submission.ServiceName);
        Assert.Equal(ProcessPhase.Evaluating, detail.Release.Phase);
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
            await context.Database.MigrateAsync();
            var checkpointCoordinator = new CheckpointStoreCoordinator(
                Directory.CreateDirectory(checkpointPath));
            return new SubmissionHarness(databasePath, checkpointPath, context, checkpointCoordinator);
        }

        public NewModel CreatePage()
        {
            var dataService = new ApplicationDataService(_context);
            var submissionService = new ReleaseSubmissionApplicationService(
                dataService,
                _checkpointCoordinator,
                new FixedTimeProvider(SubmittedAt));
            var page = new NewModel(submissionService, new FixedTimeProvider(SubmittedAt))
            {
                PageContext = new PageContext
                {
                    HttpContext = new DefaultHttpContext(),
                },
            };
            return page;
        }

        public Task<ReleaseDetailProjection?> ReadAsync(string releaseId, int revision) =>
            new ApplicationDataService(_context).GetReleaseDetailAsync(
                new ReleaseRevisionKey(releaseId, revision));

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
}
