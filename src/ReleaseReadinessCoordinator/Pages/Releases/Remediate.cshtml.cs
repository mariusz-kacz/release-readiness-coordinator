using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ReleaseReadinessCoordinator.Domain;
using ReleaseReadinessCoordinator.Workflow;

namespace ReleaseReadinessCoordinator.Pages.Releases;

public sealed class RemediateModel(
    IRemediationInteractionService interactionService,
    TimeProvider timeProvider) : PageModel
{
    public const string FeedbackKey = "WorkflowFeedback";

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public ActiveRemediationInteraction State { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(
        string releaseId,
        CancellationToken cancellationToken)
    {
        var id = ParseReleaseId(releaseId);
        if (id is null)
        {
            return NotFound();
        }

        var active = await interactionService.GetActiveAsync(id, cancellationToken);
        if (active is null)
        {
            return NotFound();
        }

        State = active;
        Input.CorrelationToken = active.CorrelationToken;
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(
        string releaseId,
        CancellationToken cancellationToken)
    {
        var id = ParseReleaseId(releaseId);
        if (id is null)
        {
            return NotFound();
        }

        var active = await interactionService.GetActiveAsync(id, cancellationToken);
        if (active is null)
        {
            return RedirectWithFeedback(
                id,
                "This remediation request is no longer active. No changes were applied.");
        }

        State = active;
        if (!ModelState.IsValid)
        {
            return Page();
        }

        IReadOnlyCollection<EvidenceRecord> evidence;
        try
        {
            evidence = Input.ToEvidence(
                active,
                new UtcInstant(timeProvider.GetUtcNow().ToUniversalTime()));
        }
        catch (Exception exception) when (exception is ArgumentException or FormatException)
        {
            ModelState.AddModelError(string.Empty, exception.Message);
            return Page();
        }

        var outcome = await interactionService.SubmitAsync(
            id,
            Input.CorrelationToken,
            evidence,
            Input.SelectedChecks(),
            cancellationToken);
        return outcome switch
        {
            RemediationSubmitOutcome.Succeeded => RedirectWithFeedback(
                id,
                "Remediation submitted. The workflow continued with a new evaluation round."),
            RemediationSubmitOutcome.CorrelationMismatch => RedirectWithFeedback(
                id,
                "This remediation form no longer matches the active workflow request. No changes were applied."),
            RemediationSubmitOutcome.NoLongerActive => RedirectWithFeedback(
                id,
                "This remediation request is no longer active. No changes were applied."),
            RemediationSubmitOutcome.TechnicalFailure => RedirectWithFeedback(
                id,
                "The workflow could not continue safely. Review the current release status before trying again."),
            RemediationSubmitOutcome.ReleaseNotFound => NotFound(),
            _ => throw new InvalidOperationException("Unknown remediation submission outcome."),
        };
    }

    public sealed record InputModel
    {
        [Required]
        [StringLength(512)]
        public string CorrelationToken { get; set; } = string.Empty;

        public bool ReplaceTestEvidence { get; set; }

        [StringLength(100)]
        public string? TestRunVersion { get; set; }

        public DateTimeOffset? TestCompletedAt { get; set; }

        [Range(0, 100)]
        public decimal? TestPassRatePercent { get; set; }

        [StringLength(4000)]
        public string? CriticalSuiteFailures { get; set; }

        public bool ReplaceSecurityEvidence { get; set; }

        [StringLength(100)]
        public string? SecurityScanVersion { get; set; }

        public DateTimeOffset? SecurityScannedAt { get; set; }

        [StringLength(4000)]
        public string? CriticalFindingIds { get; set; }

        [StringLength(4000)]
        public string? HighFindingIds { get; set; }

        [StringLength(8000)]
        public string? SecurityExceptions { get; set; }

        public bool ReplaceChangeEvidence { get; set; }

        public bool ChangeApproved { get; set; }

        public DateTimeOffset? ChangeWindowStart { get; set; }

        public DateTimeOffset? ChangeWindowEnd { get; set; }

        public bool RerunTest { get; set; }

        public bool RerunSecurity { get; set; }

        public bool RerunChange { get; set; }

        internal IReadOnlyCollection<EvidenceRecord> ToEvidence(
            ActiveRemediationInteraction active,
            UtcInstant recordedAt)
        {
            var releaseId = active.Release.Submission.ReleaseId;
            var evidence = new List<EvidenceRecord>();
            if (ReplaceTestEvidence)
            {
                var current = Current(active, EvidenceKind.Test);
                evidence.Add(new TestEvidenceRecord(
                    Guid.NewGuid(), releaseId, NextVersion(current), recordedAt, current?.Id,
                    TestRunVersion,
                    Instant(TestCompletedAt),
                    TestPassRatePercent / 100m,
                    ParseList(CriticalSuiteFailures)));
            }

            if (ReplaceSecurityEvidence)
            {
                var current = Current(active, EvidenceKind.Security);
                evidence.Add(new SecurityEvidenceRecord(
                    Guid.NewGuid(), releaseId, NextVersion(current), recordedAt, current?.Id,
                    SecurityScanVersion,
                    Instant(SecurityScannedAt),
                    ParseList(CriticalFindingIds),
                    ParseList(HighFindingIds),
                    ParseExceptions(SecurityExceptions)));
            }

            if (ReplaceChangeEvidence)
            {
                var current = Current(active, EvidenceKind.Change);
                evidence.Add(new ChangeEvidenceRecord(
                    Guid.NewGuid(), releaseId, NextVersion(current), recordedAt, current?.Id,
                    ChangeApproved,
                    OptionalInterval(
                        ChangeWindowStart,
                        ChangeWindowEnd,
                        "change approval window")));
            }

            return evidence;
        }

        internal IReadOnlyCollection<ReadinessCheck> SelectedChecks()
        {
            var checks = new List<ReadinessCheck>(3);
            if (RerunTest)
            {
                checks.Add(ReadinessCheck.Test);
            }

            if (RerunSecurity)
            {
                checks.Add(ReadinessCheck.Security);
            }

            if (RerunChange)
            {
                checks.Add(ReadinessCheck.Change);
            }

            return checks;
        }

        private static EvidenceRecord? Current(
            ActiveRemediationInteraction active,
            EvidenceKind kind) =>
            active.CurrentEvidence.GetValueOrDefault(kind);

        private static int NextVersion(EvidenceRecord? current) =>
            current is null ? 1 : checked(current.Version + 1);

        private static UtcInstant? Instant(DateTimeOffset? value) =>
            value.HasValue
                ? new UtcInstant(value.Value.ToUniversalTime())
                : null;

        private static UtcInterval? OptionalInterval(
            DateTimeOffset? start,
            DateTimeOffset? end,
            string fieldName)
        {
            if (!start.HasValue && !end.HasValue)
            {
                return null;
            }

            if (!start.HasValue || !end.HasValue)
            {
                throw new FormatException($"The {fieldName} requires both a start and an end.");
            }

            return new UtcInterval(
                new UtcInstant(start.Value.ToUniversalTime()),
                new UtcInstant(end.Value.ToUniversalTime()));
        }

        private static string[] ParseList(string? text) =>
            string.IsNullOrWhiteSpace(text)
                ? []
                : text.Split(
                    [',', '\r', '\n'],
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        private static IReadOnlyDictionary<string, (string Scope, UtcInstant ExpiresAt)>
            ParseExceptions(string? text)
        {
            var exceptions = new Dictionary<string, (string Scope, UtcInstant ExpiresAt)>(
                StringComparer.Ordinal);
            foreach (var line in Lines(text))
            {
                var fields = line.Split('|', StringSplitOptions.TrimEntries);
                if (fields.Length != 3
                    || fields.Take(2).Any(string.IsNullOrWhiteSpace)
                    || !DateTimeOffset.TryParse(fields[2], out var expiry))
                {
                    throw new FormatException(
                        "Each security exception line must use finding-id|scope|expiry-with-offset.");
                }

                if (!exceptions.TryAdd(
                    fields[0],
                    (fields[1], new UtcInstant(expiry.ToUniversalTime()))))
                {
                    throw new FormatException(
                        $"Security exceptions contain duplicate finding '{fields[0]}'.");
                }
            }

            return exceptions;
        }

        private static IEnumerable<string> Lines(string? text) =>
            string.IsNullOrWhiteSpace(text)
                ? []
                : text.Split(
                    ['\r', '\n'],
                    StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private IActionResult RedirectWithFeedback(ReleaseId releaseId, string feedback)
    {
        TempData[FeedbackKey] = feedback;
        return RedirectToPage(
            "/Releases/Detail",
            new { releaseId = releaseId.Value });
    }

    private static ReleaseId? ParseReleaseId(string releaseId)
    {
        try
        {
            return new ReleaseId(releaseId);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}
