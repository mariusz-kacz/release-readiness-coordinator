using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using ReleaseReadinessCoordinator.Data;
using ReleaseReadinessCoordinator.Domain;

namespace ReleaseReadinessCoordinator.Pages.Releases;

public sealed class DetailModel(IApplicationDataService dataService) : PageModel
{
    public ReleaseDetailProjection Detail { get; private set; } = null!;

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
