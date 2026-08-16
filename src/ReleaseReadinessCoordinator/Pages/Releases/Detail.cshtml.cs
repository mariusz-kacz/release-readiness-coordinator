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
        int revision,
        CancellationToken cancellationToken)
    {
        ReleaseRevisionKey key;
        try
        {
            key = new ReleaseRevisionKey(releaseId, revision);
        }
        catch (ArgumentException)
        {
            return NotFound();
        }

        var detail = await dataService.GetReleaseDetailAsync(key, cancellationToken);
        if (detail is null)
        {
            return NotFound();
        }

        Detail = detail;
        return Page();
    }
}
