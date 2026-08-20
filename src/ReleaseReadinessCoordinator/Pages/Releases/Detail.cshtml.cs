using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ReleaseReadinessCoordinator.Data;
using ReleaseReadinessCoordinator.Domain;
using ReleaseReadinessCoordinator.Readiness;
using ReleaseReadinessCoordinator.Workflow;
using System.Globalization;
using System.Text.RegularExpressions;

namespace ReleaseReadinessCoordinator.Pages.Releases;

public sealed partial class DetailModel(IApplicationDataService dataService) : PageModel
{
    private static readonly HashSet<string> SafeFailureKinds = new(StringComparer.Ordinal)
    {
        "Corrupt",
        "ImpossibleState",
        "Incompatible",
        "Mismatched",
        "Missing",
        "SQLite",
        "Technical",
    };

    public static IReadOnlyList<ReadinessCheck> Checks { get; } =
        [ReadinessCheck.Test, ReadinessCheck.Security, ReadinessCheck.Change];

    public ReleaseDetailProjection Detail { get; private set; } = null!;

    public EvaluationRound? LatestRound => Detail.EvaluationRounds.LastOrDefault();

    public DecisionSnapshot? LatestDecisionSnapshot => Detail.DecisionSnapshots.LastOrDefault();

    public WorkflowCorrelationRecord? ActiveWait =>
        (Detail.Release.Phase, Detail.WorkflowCorrelation?.PendingRequestKind) switch
        {
            (ProcessPhase.WaitingForRemediation, WorkflowRequestKind.Remediation) =>
                Detail.WorkflowCorrelation,
            (ProcessPhase.WaitingForApproval, WorkflowRequestKind.Approval) =>
                Detail.WorkflowCorrelation,
            _ => null,
        };

    public BranchResult? CurrentResult(ReadinessCheck check) =>
        LatestRound?.Results.Single(result => result.Check == check);

    public bool IsCurrentEvidence(EvidenceRecord evidence) =>
        Detail.CurrentEvidence.TryGetValue(evidence.Kind, out var current)
        && current.Id == evidence.Id;

    public static string FormatInstant(UtcInstant instant) =>
        LocalDateTimeDisplay.Format(instant);

    public static string FormatInstant(UtcInstant? instant) =>
        instant.HasValue ? FormatInstant(instant.Value) : "Not available";

    public static string FormatPlanningDetail(string detail)
    {
        ArgumentNullException.ThrowIfNull(detail);

        var formatted = LegacyEvidenceChangeReason().Replace(detail, match =>
        {
            var check = Enum.Parse<ReadinessCheck>(match.Groups["check"].Value);
            return RoundPlanner.ExplainEvidenceChange(
                check,
                match.Groups["previous"].Value != "<none>",
                match.Groups["current"].Value != "<none>");
        });

        return FormatText(formatted);
    }

    public static string FormatText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return EmbeddedUtcInstant().Replace(text, match =>
            TryParseUtcInstant(match.Groups["instant"].Value, out var instant)
                ? FormatInstant(new UtcInstant(instant))
                : match.Value);
    }

    private static bool TryParseUtcInstant(string text, out DateTimeOffset instant)
    {
        if (DateTimeOffset.TryParseExact(
                text,
                "yyyy-MM-dd HH:mm:ss 'UTC'",
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out instant))
        {
            return true;
        }

        if (DateTimeOffset.TryParse(
                text,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed))
        {
            instant = parsed.ToUniversalTime();
            return true;
        }

        instant = default;
        return false;
    }

    public static string FormatAttempt(string detail)
    {
        ArgumentNullException.ThrowIfNull(detail);

        var match = LegacySuccessfulAttempt().Match(detail);
        return match.Success
            && int.TryParse(match.Groups["attempt"].Value, out var attempt)
                ? EvidenceProviderRetry.SuccessfulAttemptDetail(attempt)
                : detail;
    }

    public static string SafeTimelineSummary(TimelineEntry entry)
    {
        if (entry.Kind is not TimelineEntryKind.WorkflowFailed)
        {
            return FormatPlanningDetail(entry.Summary);
        }

        const string prefix = "Workflow failed (";
        var closingParenthesis = entry.Summary.IndexOf(')', prefix.Length);
        if (entry.Summary.StartsWith(prefix, StringComparison.Ordinal)
            && closingParenthesis > prefix.Length)
        {
            var failureKind = entry.Summary[prefix.Length..closingParenthesis];
            if (SafeFailureKinds.Contains(failureKind))
            {
                return $"Workflow failed ({failureKind}).";
            }
        }

        return "Workflow failed.";
    }

    [GeneratedRegex(
        @"Executed because current (?<check>Test|Security|Change) evidence changed from (?<previous><none>|'[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}') to (?<current><none>|'[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}')\.",
        RegexOptions.CultureInvariant)]
    private static partial Regex LegacyEvidenceChangeReason();

    [GeneratedRegex(
        @"(?<![0-9])(?<instant>[0-9]{4}-[0-9]{2}-[0-9]{2}(?:T[0-9]{2}:[0-9]{2}:[0-9]{2}(?:\.[0-9]{1,7})?(?:Z|[+-][0-9]{2}:[0-9]{2})| [0-9]{2}:[0-9]{2}:[0-9]{2} UTC))(?![0-9])",
        RegexOptions.CultureInvariant)]
    private static partial Regex EmbeddedUtcInstant();

    [GeneratedRegex(@"^Attempt (?<attempt>[1-9][0-9]*) succeeded\.$", RegexOptions.CultureInvariant)]
    private static partial Regex LegacySuccessfulAttempt();

    public async Task<IActionResult> OnGetAsync(
        string releaseId,
        CancellationToken cancellationToken)
    {
        ReleaseId id;
        try
        {
            id = new ReleaseId(releaseId);
        }
        catch (ArgumentException)
        {
            return NotFound();
        }

        var detail = await dataService.GetReleaseDetailAsync(id, cancellationToken);
        if (detail is null)
        {
            return NotFound();
        }

        Detail = detail;
        return Page();
    }
}
