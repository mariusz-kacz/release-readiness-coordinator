using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ReleaseReadinessCoordinator.Domain;
using ReleaseReadinessCoordinator.Workflow;

namespace ReleaseReadinessCoordinator.Pages.Releases;

public sealed class DecisionModel(IDecisionInteractionService interactionService) : PageModel
{
    public const string FeedbackKey = "WorkflowFeedback";

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public ActiveDecisionInteraction State { get; private set; } = null!;

    public async Task<IActionResult> OnGetAsync(
        string releaseId,
        CancellationToken cancellationToken)
    {
        var id = ParseReleaseId(releaseId);
        if (id is null)
        {
            return NotFound();
        }

        var load = await interactionService.LoadAsync(id, cancellationToken);
        if (load.Outcome is DecisionLoadOutcome.Active)
        {
            State = load.Interaction!;
            Input.ResponseId = Guid.NewGuid();
            return Page();
        }

        return LoadFailure(id, load.Outcome);
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

        Input.Responder = Input.Responder?.Trim();
        Input.Comment = Input.Comment?.Trim();
        if (!Input.ResponseId.HasValue || Input.ResponseId.Value == Guid.Empty)
        {
            ModelState.TryAddModelError(
                "Input.ResponseId",
                "The decision response is invalid. Reload the decision page.");
        }

        if (!Input.Decision.HasValue || !Enum.IsDefined(Input.Decision.Value))
        {
            ModelState.TryAddModelError(
                "Input.Decision",
                "Choose Approve or Reject.");
        }

        RequireText(Input.Responder, nameof(Input.Responder), "Actor is required.");
        RequireText(Input.Comment, nameof(Input.Comment), "Comment is required.");
        if (!ModelState.IsValid)
        {
            var load = await interactionService.LoadAsync(id, cancellationToken);
            if (load.Outcome is DecisionLoadOutcome.Active)
            {
                State = load.Interaction!;
                return Page();
            }

            return LoadFailure(id, load.Outcome);
        }

        var submission = new DecisionSubmission(
            Input.ResponseId!.Value,
            Input.Decision!.Value,
            Input.Responder!,
            Input.Comment!);
        var outcome = await interactionService.SubmitAsync(
            id,
            submission,
            cancellationToken);
        return outcome switch
        {
            DecisionSubmitOutcome.Succeeded => RedirectWithFeedback(
                id,
                "Decision recorded. The release is now terminal."),
            DecisionSubmitOutcome.ExactReplay => RedirectWithFeedback(
                id,
                "This decision was already recorded. The release remains terminal."),
            DecisionSubmitOutcome.NoLongerActive => RedirectWithFeedback(
                id,
                "This approval request is no longer active. No decision was applied."),
            DecisionSubmitOutcome.ResponseConflict => RedirectWithFeedback(
                id,
                "This decision response no longer matches the recorded response. No decision was applied."),
            DecisionSubmitOutcome.TechnicalFailure => RedirectWithFeedback(
                id,
                "The workflow could not continue safely. Review the current release status before trying again."),
            DecisionSubmitOutcome.ReleaseNotFound => NotFound(),
            _ => throw new InvalidOperationException("Unknown decision submission outcome."),
        };
    }

    public sealed record InputModel
    {
        [Required]
        public Guid? ResponseId { get; set; }

        [Required]
        public HumanDecision? Decision { get; set; }

        [Required]
        [StringLength(100)]
        public string? Responder { get; set; }

        [Required]
        [StringLength(2000)]
        public string? Comment { get; set; }
    }

    private IActionResult LoadFailure(ReleaseId releaseId, DecisionLoadOutcome outcome) =>
        outcome switch
        {
            DecisionLoadOutcome.ReleaseNotFound or DecisionLoadOutcome.NoLongerActive => NotFound(),
            DecisionLoadOutcome.TechnicalFailure => RedirectWithFeedback(
                releaseId,
                "The approval request could not be restored safely. No decision was applied."),
            _ => throw new InvalidOperationException("Unknown decision load outcome."),
        };

    private IActionResult RedirectWithFeedback(ReleaseId releaseId, string feedback)
    {
        TempData[FeedbackKey] = feedback;
        return RedirectToPage(
            "/Releases/Detail",
            new { releaseId = releaseId.Value });
    }

    private void RequireText(string? value, string fieldName, string error)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            ModelState.TryAddModelError($"Input.{fieldName}", error);
        }
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
