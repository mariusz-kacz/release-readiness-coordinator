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

        var failure = await LoadStateAsync(id, cancellationToken);
        if (failure is not null)
        {
            return failure;
        }

        Input.ResponseId = Guid.NewGuid();
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
            var failure = await LoadStateAsync(id, cancellationToken);
            if (failure is not null)
            {
                return failure;
            }

            return Page();
        }

        try
        {
            var submitted = await interactionService.SubmitAsync(
                id,
                Input.ResponseId!.Value,
                Input.Decision!.Value,
                Input.Responder!,
                Input.Comment!,
                cancellationToken);
            return RedirectWithFeedback(
                id,
                submitted
                    ? "Decision recorded. The release is now terminal."
                    : "This approval request is no longer active or does not match this response. No decision was applied.");
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

    private async Task<IActionResult?> LoadStateAsync(
        ReleaseId releaseId,
        CancellationToken cancellationToken)
    {
        try
        {
            var state = await interactionService.GetActiveAsync(releaseId, cancellationToken);
            if (state is null)
            {
                return NotFound();
            }

            State = state;
            return null;
        }
        catch (WorkflowInteractionException)
        {
            return RedirectWithFeedback(
                releaseId,
                "The approval request could not be restored safely. No decision was applied.");
        }
    }

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
