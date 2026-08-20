using System.Collections.Immutable;
using System.Net;
using System.Text.RegularExpressions;
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
        Assert.DoesNotContain("name=\"Input.ReplaceTestEvidence\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"Input.ReplaceSecurityEvidence\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("name=\"Input.ReplaceChangeEvidence\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"Input.TestRunVersion\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"Input.TestPassRatePercent\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"Input.SecurityScanVersion\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"Input.SecurityExceptions\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"Input.ChangeWindowStart\"", html, StringComparison.Ordinal);
        Assert.Contains("class=\"row g-3 align-items-end\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("col-md-4 form-check ms-2 mt-5", html, StringComparison.Ordinal);
        Assert.Contains("name=\"Input.RerunTest\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"Input.RerunSecurity\"", html, StringComparison.Ordinal);
        Assert.Contains("name=\"Input.RerunChange\"", html, StringComparison.Ordinal);
        Assert.Contains("Rerun checks with unchanged evidence", html, StringComparison.Ordinal);
        Assert.Contains(
            "Changing any field for a branch appends one immutable evidence version",
            html,
            StringComparison.Ordinal);
        Assert.Contains(
            "Required fields apply only to branches you change",
            html,
            StringComparison.Ordinal);
        foreach (var requiredInput in new[]
                 {
                     "Input.TestRunVersion",
                     "Input.TestCompletedAt",
                     "Input.TestPassRatePercent",
                     "Input.SecurityScanVersion",
                     "Input.SecurityScannedAt",
                 })
        {
            var tag = InputTag(html, requiredInput);
            Assert.False(HasAttribute(tag, "required"));
            Assert.Contains("aria-required=\"false\"", tag, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("data-required-when-changed", tag, StringComparison.Ordinal);
        }
        Assert.Contains("required when approved", html, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "data-required-when=\"Input_ChangeApproved\"",
            InputTag(html, "Input.ChangeWindowStart"),
            StringComparison.Ordinal);
        Assert.Contains(
            "data-required-when-changed",
            InputTag(html, "Input.ChangeWindowStart"),
            StringComparison.Ordinal);
        Assert.Contains(
            "data-required-when=\"Input_ChangeApproved\"",
            InputTag(html, "Input.ChangeWindowEnd"),
            StringComparison.Ordinal);
        Assert.Contains(
            "Active problem &mdash; this check will rerun automatically.",
            html,
            StringComparison.Ordinal);
        Assert.Contains(
            "Passing result &mdash; it will be reused if its evidence stays unchanged and remains current.",
            html,
            StringComparison.Ordinal);
        Assert.Contains(
            "Rerun Test even if its evidence is unchanged",
            html,
            StringComparison.Ordinal);
        Assert.Contains("disabled", InputTag(html, "Input.RerunTest"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("disabled", InputTag(html, "Input.RerunSecurity"), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("disabled", InputTag(html, "Input.RerunChange"), StringComparison.OrdinalIgnoreCase);
        Assert.Contains("data-evidence-section", html, StringComparison.Ordinal);
        Assert.Contains("data-branch-status", html, StringComparison.Ordinal);
        var resetButtons = Regex.Matches(
            html,
            "<button[^>]*data-reset-evidence[^>]*>Reset changes</button>",
            RegexOptions.IgnoreCase);
        Assert.Equal(3, resetButtons.Count);
        Assert.All(
            resetButtons.Cast<Match>(),
            button => Assert.Contains("disabled", button.Value, StringComparison.OrdinalIgnoreCase));
        Assert.Contains("input.checked = initialValues[index]", html, StringComparison.Ordinal);
        Assert.Contains("input.value = initialValues[index]", html, StringComparison.Ordinal);
        Assert.Contains("let required = changed", html, StringComparison.Ordinal);
        Assert.Contains("input.required = required", html, StringComparison.Ordinal);
        Assert.Contains("Save evidence and run next evaluation", html, StringComparison.Ordinal);
        Assert.Contains("Load ready demo evidence", html, StringComparison.Ordinal);
        Assert.Contains("demo=ready", html, StringComparison.Ordinal);
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
            TestRunVersion = "2026.08.19",
            TestCompletedAt = Now.Value,
            TestPassRatePercent = 99m,
            CriticalSuiteFailures = string.Empty,
            SecurityScanVersion = "2026.08.19",
            SecurityScannedAt = Now.Value,
            CriticalFindingIds = string.Empty,
            HighFindingIds = "HIGH-7",
            SecurityExceptions = "HIGH-7|payments-api|2026-08-20T10:00:00Z",
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
    public async Task Active_request_prefills_current_evidence_for_focused_corrections()
    {
        var state = ActiveState() with
        {
            CurrentEvidence = new Dictionary<EvidenceKind, EvidenceRecord>
            {
                [EvidenceKind.Test] = new TestEvidenceRecord(
                    Guid.NewGuid(), new ReleaseId("release-remediation"), 2, Now, Guid.NewGuid(),
                    "2026.08.19", Now, null, ["checkout"]),
                [EvidenceKind.Security] = new SecurityEvidenceRecord(
                    Guid.NewGuid(), new ReleaseId("release-remediation"), 2, Now, Guid.NewGuid(),
                    "2026.08.19", null, ["CRIT-1"], ["HIGH-1"],
                    new Dictionary<string, (string Scope, UtcInstant ExpiresAt)>
                    {
                        ["HIGH-1"] = ("payments-api", Now),
                    }),
            }.ToImmutableDictionary(),
        };
        var service = new StubRemediationInteractionService(state);
        var page = CreatePage(service);

        var result = await page.OnGetAsync("release-remediation", CancellationToken.None);

        Assert.IsType<PageResult>(result);
        Assert.Equal("2026.08.19", page.Input.TestRunVersion);
        Assert.Equal(Now.Value, page.Input.TestCompletedAt);
        Assert.Null(page.Input.TestPassRatePercent);
        Assert.Equal("checkout", page.Input.CriticalSuiteFailures);
        Assert.Equal("2026.08.19", page.Input.SecurityScanVersion);
        Assert.Null(page.Input.SecurityScannedAt);
        Assert.Equal("CRIT-1", page.Input.CriticalFindingIds);
        Assert.Equal("HIGH-1", page.Input.HighFindingIds);
        Assert.Equal(
            "HIGH-1|payments-api|2026-08-19 09:00:00 UTC",
            page.Input.SecurityExceptions);

        page.Input.TestPassRatePercent = 99m;
        page.Input.SecurityScannedAt = Now.Value;
        await page.OnPostAsync("release-remediation", CancellationToken.None);

        var submission = Assert.Single(service.Submissions);
        Assert.Collection(
            submission.Evidence,
            item =>
            {
                var test = Assert.IsType<TestEvidenceRecord>(item);
                Assert.Equal("2026.08.19", test.TestRunVersion);
                Assert.Equal(Now, test.CompletedAt);
                Assert.Equal(0.99m, test.PassRate);
                Assert.Equal("checkout", Assert.Single(test.CriticalSuiteFailures!.Value));
            },
            item =>
            {
                var security = Assert.IsType<SecurityEvidenceRecord>(item);
                Assert.Equal("2026.08.19", security.ScanVersion);
                Assert.Equal(Now, security.ScannedAt);
                Assert.Equal("CRIT-1", Assert.Single(security.UnresolvedCriticalFindingIds!.Value));
                Assert.Equal("HIGH-1", Assert.Single(security.UnresolvedHighFindingIds!.Value));
                Assert.Equal("payments-api", security.ApprovedExceptions!["HIGH-1"].Scope);
            });
    }

    [Fact]
    public async Task Ready_demo_prefill_uses_active_release_facts_without_submitting()
    {
        var state = ActiveState();
        var service = new StubRemediationInteractionService(state);
        var page = CreatePage(service);

        var result = await page.OnGetAsync(
            "release-remediation",
            CancellationToken.None,
            demo: "ready");

        Assert.IsType<PageResult>(result);
        Assert.Equal(state.CorrelationToken, page.Input.CorrelationToken);
        Assert.Equal("2026.08.19", page.Input.TestRunVersion);
        Assert.Equal(Now.Value, page.Input.TestCompletedAt);
        Assert.Equal(99m, page.Input.TestPassRatePercent);
        Assert.Equal(string.Empty, page.Input.CriticalSuiteFailures);
        Assert.Equal("2026.08.19", page.Input.SecurityScanVersion);
        Assert.Equal(Now.Value, page.Input.SecurityScannedAt);
        Assert.Equal(string.Empty, page.Input.CriticalFindingIds);
        Assert.Equal(string.Empty, page.Input.HighFindingIds);
        Assert.Equal(string.Empty, page.Input.SecurityExceptions);
        Assert.False(page.Input.ChangeApproved);
        Assert.Null(page.Input.ChangeWindowStart);
        Assert.Null(page.Input.ChangeWindowEnd);
        Assert.Empty(service.Submissions);
    }

    [Fact]
    public async Task Ready_demo_preserves_evidence_for_checks_without_active_problems()
    {
        var state = ActiveState(includeCurrentEvidence: true);
        state = state with
        {
            Request = new RemediationRequest(
                state.Request.Id,
                state.Release.Submission.ReleaseId,
                state.Request.RoundNumber,
                state.Request.CreatedAt,
                [
                    Problem(state.Release.Submission.ReleaseId, ReadinessCheck.Security, "Security evidence is missing"),
                    Problem(state.Release.Submission.ReleaseId, ReadinessCheck.Change, "Change evidence is blocked"),
                ]),
        };
        var currentTest = Assert.IsType<TestEvidenceRecord>(
            state.CurrentEvidence[EvidenceKind.Test]);
        var service = new StubRemediationInteractionService(state);
        var page = CreatePage(service);

        await page.OnGetAsync(
            "release-remediation",
            CancellationToken.None,
            demo: "ready");
        await page.OnPostAsync("release-remediation", CancellationToken.None);

        Assert.Equal(currentTest.TestRunVersion, page.Input.TestRunVersion);
        Assert.Equal(currentTest.CompletedAt?.Value, page.Input.TestCompletedAt);
        Assert.Equal(currentTest.PassRate * 100m, page.Input.TestPassRatePercent);
        var submission = Assert.Single(service.Submissions);
        Assert.DoesNotContain(
            submission.Evidence,
            evidence => evidence.Kind is EvidenceKind.Test);
        Assert.Equal(
            [EvidenceKind.Security, EvidenceKind.Change],
            submission.Evidence.Select(evidence => evidence.Kind));
    }

    [Fact]
    public async Task Explicit_rerun_with_unchanged_prefilled_evidence_does_not_create_versions()
    {
        var state = ActiveState(includeCurrentEvidence: true);
        var service = new StubRemediationInteractionService(state);
        var page = CreatePage(service);
        await page.OnGetAsync("release-remediation", CancellationToken.None);
        page.Input.RerunTest = true;

        await page.OnPostAsync("release-remediation", CancellationToken.None);

        var submission = Assert.Single(service.Submissions);
        Assert.Empty(submission.Evidence);
        Assert.Equal([ReadinessCheck.Test], submission.SelectedChecks);
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

    [Fact]
    public async Task Stale_or_mismatched_submission_redirects_with_safe_feedback()
    {
        var state = ActiveState();
        var service = new StubRemediationInteractionService(
            state,
            submitResult: false);
        var page = CreatePage(service);
        page.Input.CorrelationToken = "posted-correlation";

        var result = await page.OnPostAsync("release-remediation", CancellationToken.None);

        Assert.IsType<RedirectToPageResult>(result);
        Assert.Contains(
            "no changes were applied",
            Assert.IsType<string>(page.TempData["WorkflowFeedback"]),
            StringComparison.OrdinalIgnoreCase);
        Assert.Single(service.Submissions);
        Assert.Equal("posted-correlation", service.Submissions[0].CorrelationToken);
    }

    [Fact]
    public async Task Technical_failure_redirects_with_safe_feedback()
    {
        var state = ActiveState();
        var service = new StubRemediationInteractionService(
            state,
            failSubmit: true);
        var page = CreatePage(service);
        page.Input.CorrelationToken = state.CorrelationToken;

        var result = await page.OnPostAsync(
            "release-remediation",
            CancellationToken.None);

        Assert.IsType<RedirectToPageResult>(result);
        Assert.Contains(
            "could not continue safely",
            Assert.IsType<string>(page.TempData["WorkflowFeedback"]),
            StringComparison.OrdinalIgnoreCase);
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

        Assert.False(mismatch);
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

        Assert.True(first);
        Assert.False(duplicate);
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

    private static string InputTag(string html, string name) =>
        Regex.Match(
            html,
            $"<input[^>]*name=\"{Regex.Escape(name)}\"[^>]*>",
            RegexOptions.IgnoreCase).Value;

    private static bool HasAttribute(string tag, string attribute) =>
        Regex.IsMatch(
            tag,
            $@"\s{Regex.Escape(attribute)}(?:\s|=|/?>)",
            RegexOptions.IgnoreCase);

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
            evidence.ToImmutableDictionary(),
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
        bool submitResult = true,
        bool failSubmit = false)
        : IRemediationInteractionService
    {
        public List<SubmittedRemediation> Submissions { get; } = [];

        public Task<ActiveRemediationInteraction?> GetActiveAsync(
            ReleaseId releaseId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(active?.Release.Submission.ReleaseId == releaseId ? active : null);

        public Task<bool> SubmitAsync(
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
            if (failSubmit)
            {
                throw new WorkflowInteractionException(new InvalidOperationException());
            }

            return Task.FromResult(submitResult);
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
