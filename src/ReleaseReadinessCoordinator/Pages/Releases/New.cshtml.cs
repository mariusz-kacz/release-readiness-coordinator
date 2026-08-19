using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ReleaseReadinessCoordinator.Data;
using ReleaseReadinessCoordinator.Domain;
using ReleaseReadinessCoordinator.Simulation;
using ReleaseReadinessCoordinator.Workflow;
using static ReleaseReadinessCoordinator.Pages.Releases.EvidenceFormParsing;

namespace ReleaseReadinessCoordinator.Pages.Releases;

public sealed class NewModel(
    ReleaseSubmissionApplicationService submissionService,
    IApplicationDataService dataService,
    TimeProvider timeProvider) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty(SupportsGet = true)]
    [Display(Name = "Release ID")]
    [StringLength(200)]
    public string ExistingReleaseId { get; set; } = string.Empty;

    public IReadOnlyList<DemoReleaseFixture> Fixtures => DemoReleaseFixtures.All;

    public async Task<IActionResult> OnGetAsync(
        string? fixture,
        CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(ExistingReleaseId))
        {
            return await ContinueAsync(cancellationToken);
        }

        var selected = DemoReleaseFixtures.Find(fixture);
        if (selected is not null)
        {
            Input = InputModel.FromFixture(selected);
        }

        return Page();
    }

    private async Task<IActionResult> ContinueAsync(
        CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        ReleaseId releaseId;
        try
        {
            releaseId = new ReleaseId(ExistingReleaseId);
        }
        catch (ArgumentException)
        {
            ModelState.AddModelError(
                nameof(ExistingReleaseId),
                "Enter a valid release ID.");
            return Page();
        }

        var detail = await dataService.GetReleaseDetailAsync(
            releaseId,
            cancellationToken);
        if (detail is null)
        {
            ModelState.AddModelError(
                nameof(ExistingReleaseId),
                $"Release '{releaseId}' was not found.");
            return Page();
        }

        var page = detail.Release.Phase switch
        {
            ProcessPhase.WaitingForRemediation => "/Releases/Remediate",
            ProcessPhase.WaitingForApproval => "/Releases/Decision",
            _ => "/Releases/Detail",
        };
        return RedirectToPage(page, new { releaseId = releaseId.Value });
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        try
        {
            var now = new UtcInstant(timeProvider.GetUtcNow());
            var submission = Input.ToSubmission(now);
            var evidence = Input.ToEvidence(submission.ReleaseId, now);
            await submissionService.SubmitAsync(submission, evidence, cancellationToken);
            return RedirectToPage(
                "/Releases/Detail",
                new { releaseId = submission.ReleaseId.Value });
        }
        catch (ApplicationDataConflictException exception)
            when (exception.Kind is ApplicationDataConflictKind.DuplicateReleaseId
                or ApplicationDataConflictKind.TerminalRelease)
        {
            Response.StatusCode = StatusCodes.Status409Conflict;
            ModelState.AddModelError(string.Empty, exception.Message);
            return Page();
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            return Page();
        }
    }

    public sealed record InputModel
    {
        [Required]
        public string ReleaseId { get; set; } = string.Empty;

        [Required]
        public string ServiceName { get; set; } = string.Empty;

        [Required]
        public string ReleaseVersion { get; set; } = string.Empty;

        public DateTimeOffset DeploymentWindowStart { get; set; }

        public DateTimeOffset DeploymentWindowEnd { get; set; }

        public bool IncludeTestEvidence { get; set; }

        public string? TestRunVersion { get; set; }

        public DateTimeOffset? TestCompletedAt { get; set; }

        [Range(0, 100)]
        public decimal? TestPassRatePercent { get; set; }

        public string? CriticalSuiteFailures { get; set; }

        public bool IncludeSecurityEvidence { get; set; }

        public string? SecurityScanVersion { get; set; }

        public DateTimeOffset? SecurityScannedAt { get; set; }

        public string? CriticalFindingIds { get; set; }

        public string? HighFindingIds { get; set; }

        public string? SecurityExceptions { get; set; }

        public bool IncludeChangeEvidence { get; set; }

        public bool ChangeApproved { get; set; }

        public DateTimeOffset? ChangeWindowStart { get; set; }

        public DateTimeOffset? ChangeWindowEnd { get; set; }

        public static InputModel FromFixture(DemoReleaseFixture fixture) => new()
        {
            ReleaseId = fixture.ReleaseId,
            ServiceName = fixture.ServiceName,
            ReleaseVersion = fixture.ReleaseVersion,
            DeploymentWindowStart = fixture.DeploymentWindowStart,
            DeploymentWindowEnd = fixture.DeploymentWindowEnd,
            IncludeTestEvidence = fixture.IncludeTestEvidence,
            TestRunVersion = fixture.TestRunVersion,
            TestCompletedAt = fixture.TestCompletedAt,
            TestPassRatePercent = fixture.TestPassRatePercent,
            CriticalSuiteFailures = fixture.CriticalSuiteFailures,
            IncludeSecurityEvidence = fixture.IncludeSecurityEvidence,
            SecurityScanVersion = fixture.SecurityScanVersion,
            SecurityScannedAt = fixture.SecurityScannedAt,
            CriticalFindingIds = fixture.CriticalFindingIds,
            HighFindingIds = fixture.HighFindingIds,
            SecurityExceptions = fixture.SecurityExceptions,
            IncludeChangeEvidence = fixture.IncludeChangeEvidence,
            ChangeApproved = fixture.ChangeApproved,
            ChangeWindowStart = fixture.ChangeWindowStart,
            ChangeWindowEnd = fixture.ChangeWindowEnd,
        };

        internal ReleaseSubmission ToSubmission(UtcInstant submittedAt)
        {
            var releaseId = new Domain.ReleaseId(ReleaseId);
            return new ReleaseSubmission(
                releaseId,
                ServiceName,
                ReleaseVersion,
                Interval(DeploymentWindowStart, DeploymentWindowEnd),
                submittedAt);
        }

        internal IReadOnlyCollection<EvidenceRecord> ToEvidence(
            Domain.ReleaseId releaseId,
            UtcInstant recordedAt)
        {
            var evidence = new List<EvidenceRecord>();
            if (IncludeTestEvidence)
            {
                evidence.Add(new TestEvidenceRecord(
                    Guid.NewGuid(), releaseId, 1, recordedAt, null,
                    TestRunVersion,
                    Instant(TestCompletedAt),
                    TestPassRatePercent / 100m,
                    ParseList(CriticalSuiteFailures)));
            }

            if (IncludeSecurityEvidence)
            {
                evidence.Add(new SecurityEvidenceRecord(
                    Guid.NewGuid(), releaseId, 1, recordedAt, null,
                    SecurityScanVersion,
                    Instant(SecurityScannedAt),
                    ParseList(CriticalFindingIds),
                    ParseList(HighFindingIds),
                    ParseExceptions(SecurityExceptions)));
            }

            if (IncludeChangeEvidence)
            {
                evidence.Add(new ChangeEvidenceRecord(
                    Guid.NewGuid(), releaseId, 1, recordedAt, null,
                    ChangeApproved,
                    OptionalInterval(ChangeWindowStart, ChangeWindowEnd, "change approval window")));
            }

            return evidence;
        }

    }
}
