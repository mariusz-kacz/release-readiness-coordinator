using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ReleaseReadinessCoordinator.Data;
using ReleaseReadinessCoordinator.Domain;

namespace ReleaseReadinessCoordinator.Pages.Releases;

public sealed class DetailModel(IApplicationDataService dataService) : PageModel
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
        instant.Value.ToString("yyyy-MM-dd HH:mm 'UTC'");

    public static string FormatInstant(UtcInstant? instant) =>
        instant.HasValue ? FormatInstant(instant.Value) : "Not available";

    public static string SafeTimelineSummary(TimelineEntry entry)
    {
        if (entry.Kind is not TimelineEntryKind.WorkflowFailed)
        {
            return entry.Summary;
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
