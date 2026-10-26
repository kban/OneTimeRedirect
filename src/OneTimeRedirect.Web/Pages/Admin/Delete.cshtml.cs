using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace OneTimeRedirect.Web.Pages.Admin;

/// <summary>
/// Server-side confirmation for permanent deletion. GET only shows the question; the deletion itself is a POST
/// (antiforgery-protected) and the SQL statement re-checks the status, so the page is a convenience, not the guard.
/// </summary>
public sealed class DeleteModel(LinkStore store) : PageModel
{
    // List state to return to (filters, search, page). Never secrets.
    [BindProperty(SupportsGet = true)] public string? Status { get; set; }
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public int P { get; set; } = 1;

    public LinkSummary? Link { get; private set; }
    public LinkStatus LinkState { get; private set; }

    public async Task<IActionResult> OnGetAsync(long id, CancellationToken ct)
    {
        Link = await store.GetAsync(id, ct);
        if (Link is null) return Back("Ссылка не найдена: возможно, она уже удалена.");
        LinkState = Link.GetStatus(DateTimeOffset.UtcNow);
        if (LinkState == LinkStatus.Active) return Back("Активную ссылку нельзя удалить: сначала отзовите её.");
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(long id, CancellationToken ct) =>
        Back(await store.DeleteAsync(id, ct)
            ? "Ссылка удалена"
            : "Ссылку нельзя удалить: она активна или уже удалена.");

    private RedirectToPageResult Back(string message)
    {
        TempData["Message"] = message;
        // The list clamps a page number that no longer exists, so deleting the last row of a page lands on the previous one.
        return RedirectToPage("/Admin/Index", new { status = Status, q = Q, p = P });
    }
}
