using System.Collections.Immutable;
using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ReleaseReadinessCoordinator.Domain;
using ReleaseReadinessCoordinator.Workflow;
using static ReleaseReadinessCoordinator.Pages.Releases.EvidenceFormParsing;

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
        CancellationToken cancellationToken,
        string? demo = null)
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
        Input = InputModel.From(active);
        if (string.Equals(demo, "ready", StringComparison.OrdinalIgnoreCase))
        {
            Input.ApplyReadyDemo(
                active,
                new UtcInstant(timeProvider.GetUtcNow().ToUniversalTime()));
        }

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

        try
        {
            var submitted = await interactionService.SubmitAsync(
                id,
                Input.CorrelationToken,
                evidence,
                Input.SelectedChecks(),
                cancellationToken);
            return RedirectWithFeedback(
                id,
                submitted
                    ? "Remediation submitted. The workflow continued with a new evaluation round."
                    : "This remediation request is no longer active or does not match this form. No changes were applied.");
        }
        catch (WorkflowInteractionException)
        {
            return RedirectWithFeedback(
                id,
                "The workflow could not continue safely. Review the current release status before trying again.");
        }
    }

    public sealed record InputModel
    {
        [Required]
        [StringLength(512)]
        public string CorrelationToken { get; set; } = string.Empty;

        [StringLength(100)]
        public string? TestRunVersion { get; set; }

        public DateTimeOffset? TestCompletedAt { get; set; }

        [Range(0, 100)]
        public decimal? TestPassRatePercent { get; set; }

        [StringLength(4000)]
        public string? CriticalSuiteFailures { get; set; }

        [StringLength(100)]
        public string? SecurityScanVersion { get; set; }

        public DateTimeOffset? SecurityScannedAt { get; set; }

        [StringLength(4000)]
        public string? CriticalFindingIds { get; set; }

        [StringLength(4000)]
        public string? HighFindingIds { get; set; }

        [StringLength(8000)]
        public string? SecurityExceptions { get; set; }

        public bool ChangeApproved { get; set; }

        public DateTimeOffset? ChangeWindowStart { get; set; }

        public DateTimeOffset? ChangeWindowEnd { get; set; }

        public bool RerunTest { get; set; }

        public bool RerunSecurity { get; set; }

        public bool RerunChange { get; set; }

        internal static InputModel From(ActiveRemediationInteraction active)
        {
            var input = new InputModel { CorrelationToken = active.CorrelationToken };
            if (Current(active, EvidenceKind.Test) is TestEvidenceRecord test)
            {
                input.TestRunVersion = test.TestRunVersion;
                input.TestCompletedAt = test.CompletedAt?.Value;
                input.TestPassRatePercent = test.PassRate * 100m;
                input.CriticalSuiteFailures = FormatList(test.CriticalSuiteFailures);
            }

            if (Current(active, EvidenceKind.Security) is SecurityEvidenceRecord security)
            {
                input.SecurityScanVersion = security.ScanVersion;
                input.SecurityScannedAt = security.ScannedAt?.Value;
                input.CriticalFindingIds = FormatList(security.UnresolvedCriticalFindingIds);
                input.HighFindingIds = FormatList(security.UnresolvedHighFindingIds);
                input.SecurityExceptions = FormatExceptions(security.ApprovedExceptions);
            }

            if (Current(active, EvidenceKind.Change) is ChangeEvidenceRecord change)
            {
                input.ChangeApproved = change.IsApproved is true;
                input.ChangeWindowStart = change.ApprovedWindow?.Start.Value;
                input.ChangeWindowEnd = change.ApprovedWindow?.End.Value;
            }

            return input;
        }

        internal void ApplyReadyDemo(ActiveRemediationInteraction active, UtcInstant now)
        {
            var problemChecks = active.Request.Problems
                .Select(problem => problem.Check)
                .ToHashSet();
            var submission = active.Release.Submission;
            if (problemChecks.Contains(ReadinessCheck.Test))
            {
                TestRunVersion = submission.ReleaseVersion;
                TestCompletedAt = now.Value;
                TestPassRatePercent = 99m;
                CriticalSuiteFailures = string.Empty;
            }

            if (problemChecks.Contains(ReadinessCheck.Security))
            {
                SecurityScanVersion = submission.ReleaseVersion;
                SecurityScannedAt = now.Value;
                CriticalFindingIds = string.Empty;
                HighFindingIds = string.Empty;
                SecurityExceptions = string.Empty;
            }

            if (problemChecks.Contains(ReadinessCheck.Change))
            {
                ChangeApproved = true;
                ChangeWindowStart = submission.RequestedDeploymentWindow.Start.Value;
                ChangeWindowEnd = submission.RequestedDeploymentWindow.End.Value;
            }
        }

        internal IReadOnlyCollection<EvidenceRecord> ToEvidence(
            ActiveRemediationInteraction active,
            UtcInstant recordedAt)
        {
            var releaseId = active.Release.Submission.ReleaseId;
            var evidence = new List<EvidenceRecord>();
            if (HasTestEvidenceChanges(active))
            {
                var current = Current(active, EvidenceKind.Test);
                evidence.Add(new TestEvidenceRecord(
                    Guid.NewGuid(), releaseId, NextVersion(current), recordedAt, current?.Id,
                    TestRunVersion,
                    Instant(TestCompletedAt),
                    TestPassRatePercent / 100m,
                    ParseList(CriticalSuiteFailures)));
            }

            if (HasSecurityEvidenceChanges(active))
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

            if (HasChangeEvidenceChanges(active))
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

        private bool HasTestEvidenceInput() =>
            !string.IsNullOrWhiteSpace(TestRunVersion)
            || TestCompletedAt.HasValue
            || TestPassRatePercent.HasValue
            || !string.IsNullOrWhiteSpace(CriticalSuiteFailures);

        private bool HasSecurityEvidenceInput() =>
            !string.IsNullOrWhiteSpace(SecurityScanVersion)
            || SecurityScannedAt.HasValue
            || !string.IsNullOrWhiteSpace(CriticalFindingIds)
            || !string.IsNullOrWhiteSpace(HighFindingIds)
            || !string.IsNullOrWhiteSpace(SecurityExceptions);

        private bool HasTestEvidenceChanges(ActiveRemediationInteraction active)
        {
            if (Current(active, EvidenceKind.Test) is not TestEvidenceRecord current)
            {
                return HasTestEvidenceInput();
            }

            return !string.Equals(TestRunVersion?.Trim(), current.TestRunVersion, StringComparison.Ordinal)
                || Instant(TestCompletedAt) != current.CompletedAt
                || TestPassRatePercent / 100m != current.PassRate
                || !SameItems(ParseList(CriticalSuiteFailures), current.CriticalSuiteFailures);
        }

        private bool HasSecurityEvidenceChanges(ActiveRemediationInteraction active)
        {
            if (Current(active, EvidenceKind.Security) is not SecurityEvidenceRecord current)
            {
                return HasSecurityEvidenceInput();
            }

            return !string.Equals(SecurityScanVersion?.Trim(), current.ScanVersion, StringComparison.Ordinal)
                || Instant(SecurityScannedAt) != current.ScannedAt
                || !SameItems(ParseList(CriticalFindingIds), current.UnresolvedCriticalFindingIds)
                || !SameItems(ParseList(HighFindingIds), current.UnresolvedHighFindingIds)
                || !SameExceptions(ParseExceptions(SecurityExceptions), current.ApprovedExceptions);
        }

        private bool HasChangeEvidenceChanges(ActiveRemediationInteraction active)
        {
            if (Current(active, EvidenceKind.Change) is not ChangeEvidenceRecord current)
            {
                return ChangeApproved || ChangeWindowStart.HasValue || ChangeWindowEnd.HasValue;
            }

            return ChangeApproved != (current.IsApproved ?? false)
                || OptionalInterval(
                    ChangeWindowStart,
                    ChangeWindowEnd,
                    "change approval window") != current.ApprovedWindow;
        }

        private static bool SameItems(
            IEnumerable<string> posted,
            ImmutableArray<string>? current) =>
            posted.SequenceEqual(current ?? [], StringComparer.Ordinal);

        private static bool SameExceptions(
            IReadOnlyDictionary<string, (string Scope, UtcInstant ExpiresAt)> posted,
            IReadOnlyDictionary<string, (string Scope, UtcInstant ExpiresAt)>? current) =>
            posted.Count == (current?.Count ?? 0)
            && posted.All(pair =>
                current is not null
                && current.TryGetValue(pair.Key, out var value)
                && value == pair.Value);

        private static string? FormatList(ImmutableArray<string>? values) =>
            values.HasValue ? string.Join(", ", values.Value) : null;

        private static string? FormatExceptions(
            IReadOnlyDictionary<string, (string Scope, UtcInstant ExpiresAt)>? exceptions) =>
            exceptions is null
                ? null
                : string.Join(
                    Environment.NewLine,
                    exceptions
                        .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                        .Select(pair =>
                            $"{pair.Key}|{pair.Value.Scope}|{pair.Value.ExpiresAt.ToDisplayString()}"));

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
