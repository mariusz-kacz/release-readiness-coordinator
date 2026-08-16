using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ReleaseReadinessCoordinator.Data;
using ReleaseReadinessCoordinator.Domain;
using ReleaseReadinessCoordinator.Simulation;
using ReleaseReadinessCoordinator.Workflow;

namespace ReleaseReadinessCoordinator.Pages.Releases;

public sealed class NewModel(
    ReleaseSubmissionApplicationService submissionService,
    TimeProvider timeProvider) : PageModel
{
    [BindProperty]
    public InputModel Input { get; set; } = new();

    public IReadOnlyList<DemoReleaseFixture> Fixtures => DemoReleaseFixtures.All;

    public void OnGet(string? fixture)
    {
        var selected = DemoReleaseFixtures.Find(fixture);
        if (selected is not null)
        {
            Input = InputModel.FromFixture(selected);
        }
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
            var evidence = Input.ToEvidence(submission.Key, now);
            await submissionService.SubmitAsync(submission, evidence, cancellationToken);
            return RedirectToPage(
                "/Releases/Detail",
                new { releaseId = submission.Key.ReleaseId, revision = submission.Key.Revision });
        }
        catch (ApplicationDataConflictException exception)
            when (exception.Kind is ApplicationDataConflictKind.DuplicateReleaseRevision
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

        [Range(1, int.MaxValue)]
        public int Revision { get; set; } = 1;

        [Required]
        public string ServiceName { get; set; } = string.Empty;

        [Required]
        public string ReleaseVersion { get; set; } = string.Empty;

        public DateTimeOffset DeploymentWindowStart { get; set; }

        public DateTimeOffset DeploymentWindowEnd { get; set; }

        [Required]
        public string DependencyRequirements { get; set; } = string.Empty;

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

        public bool IncludeDependencyEvidence { get; set; }

        public DateTimeOffset? DependencyObservedAt { get; set; }

        public string? DependencyStates { get; set; }

        public static InputModel FromFixture(DemoReleaseFixture fixture) => new()
        {
            ReleaseId = fixture.ReleaseId,
            Revision = fixture.Revision,
            ServiceName = fixture.ServiceName,
            ReleaseVersion = fixture.ReleaseVersion,
            DeploymentWindowStart = fixture.DeploymentWindowStart,
            DeploymentWindowEnd = fixture.DeploymentWindowEnd,
            DependencyRequirements = string.Join(
                Environment.NewLine,
                fixture.DependencyRequirements.Select(pair => $"{pair.Key}={pair.Value}")),
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
            IncludeDependencyEvidence = fixture.IncludeDependencyEvidence,
            DependencyObservedAt = fixture.DependencyObservedAt,
            DependencyStates = fixture.DependencyStates,
        };

        internal ReleaseSubmission ToSubmission(UtcInstant submittedAt)
        {
            var key = new ReleaseRevisionKey(ReleaseId, Revision);
            return new ReleaseSubmission(
                key,
                ServiceName,
                ReleaseVersion,
                Interval(DeploymentWindowStart, DeploymentWindowEnd),
                ParseKeyValues(DependencyRequirements, "dependency requirements"),
                submittedAt);
        }

        internal IReadOnlyCollection<EvidenceRecord> ToEvidence(
            ReleaseRevisionKey key,
            UtcInstant recordedAt)
        {
            var evidence = new List<EvidenceRecord>();
            if (IncludeTestEvidence)
            {
                evidence.Add(new TestEvidenceRecord(
                    Guid.NewGuid(), key, 1, recordedAt, null,
                    TestRunVersion,
                    Instant(TestCompletedAt),
                    TestPassRatePercent / 100m,
                    ParseList(CriticalSuiteFailures)));
            }

            if (IncludeSecurityEvidence)
            {
                evidence.Add(new SecurityEvidenceRecord(
                    Guid.NewGuid(), key, 1, recordedAt, null,
                    SecurityScanVersion,
                    Instant(SecurityScannedAt),
                    ParseList(CriticalFindingIds),
                    ParseList(HighFindingIds),
                    ParseExceptions(SecurityExceptions)));
            }

            if (IncludeChangeEvidence)
            {
                evidence.Add(new ChangeEvidenceRecord(
                    Guid.NewGuid(), key, 1, recordedAt, null,
                    ChangeApproved,
                    OptionalInterval(ChangeWindowStart, ChangeWindowEnd, "change approval window")));
            }

            if (IncludeDependencyEvidence)
            {
                evidence.Add(new DependencyEvidenceRecord(
                    Guid.NewGuid(), key, 1, recordedAt, null,
                    Instant(DependencyObservedAt),
                    ParseDependencyStates(DependencyStates)));
            }

            return evidence;
        }

        private static UtcInstant Instant(DateTimeOffset value) =>
            new(value.ToUniversalTime());

        private static UtcInstant? Instant(DateTimeOffset? value) =>
            value.HasValue ? Instant(value.Value) : null;

        private static UtcInterval Interval(DateTimeOffset start, DateTimeOffset end) =>
            new(Instant(start), Instant(end));

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

            return Interval(start.Value, end.Value);
        }

        private static IReadOnlyDictionary<string, string> ParseKeyValues(string text, string fieldName)
        {
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var line in Lines(text))
            {
                var fields = line.Split('=', 2, StringSplitOptions.TrimEntries);
                if (fields.Length != 2 || fields.Any(string.IsNullOrWhiteSpace))
                {
                    throw new FormatException($"Each {fieldName} line must use name=value.");
                }

                if (!values.TryAdd(fields[0], fields[1]))
                {
                    throw new FormatException($"The {fieldName} contain duplicate name '{fields[0]}'.");
                }
            }

            return values;
        }

        private static string[] ParseList(string? text) =>
            string.IsNullOrWhiteSpace(text)
                ? []
                : text.Split([',', '\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        private static IReadOnlyDictionary<string, (string Scope, UtcInstant ExpiresAt)> ParseExceptions(
            string? text)
        {
            var exceptions = new Dictionary<string, (string Scope, UtcInstant ExpiresAt)>(StringComparer.Ordinal);
            foreach (var line in Lines(text))
            {
                var fields = line.Split('|', StringSplitOptions.TrimEntries);
                if (fields.Length != 3 || fields.Take(2).Any(string.IsNullOrWhiteSpace)
                    || !DateTimeOffset.TryParse(fields[2], out var expiry))
                {
                    throw new FormatException(
                        "Each security exception line must use finding-id|scope|expiry-with-offset.");
                }

                if (!exceptions.TryAdd(fields[0], (fields[1], Instant(expiry))))
                {
                    throw new FormatException($"Security exceptions contain duplicate finding '{fields[0]}'.");
                }
            }

            return exceptions;
        }

        private static IReadOnlyDictionary<string, DependencyState> ParseDependencyStates(string? text)
        {
            var dependencies = new Dictionary<string, DependencyState>(StringComparer.Ordinal);
            foreach (var line in Lines(text))
            {
                var fields = line.Split('|', StringSplitOptions.TrimEntries);
                if (fields.Length != 6 || string.IsNullOrWhiteSpace(fields[0]))
                {
                    throw new FormatException(
                        "Each dependency evidence line must use name|available-version|available-start|available-end|maintenance-start|maintenance-end.");
                }

                var availability = ParseCompactInterval(fields[2], fields[3], "dependency availability");
                var maintenance = ParseCompactInterval(fields[4], fields[5], "dependency maintenance");
                if (!dependencies.TryAdd(
                    fields[0],
                    new DependencyState(
                        NullIfEmpty(fields[1]),
                        availability is null ? [] : [availability],
                        maintenance is null ? [] : [maintenance])))
                {
                    throw new FormatException($"Dependency evidence contains duplicate name '{fields[0]}'.");
                }
            }

            return dependencies;
        }

        private static UtcInterval? ParseCompactInterval(string start, string end, string fieldName)
        {
            if (string.IsNullOrWhiteSpace(start) && string.IsNullOrWhiteSpace(end))
            {
                return null;
            }

            if (!DateTimeOffset.TryParse(start, out var parsedStart)
                || !DateTimeOffset.TryParse(end, out var parsedEnd))
            {
                throw new FormatException($"The {fieldName} requires valid start and end timestamps with offsets.");
            }

            return Interval(parsedStart, parsedEnd);
        }

        private static IEnumerable<string> Lines(string? text) =>
            string.IsNullOrWhiteSpace(text)
                ? []
                : text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        private static string? NullIfEmpty(string value) =>
            string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
