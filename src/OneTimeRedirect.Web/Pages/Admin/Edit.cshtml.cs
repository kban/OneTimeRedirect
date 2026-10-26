using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace OneTimeRedirect.Web.Pages.Admin;

public sealed class EditModel(LinkStore store) : PageModel
{
    public long LinkId { get; private set; }
    public LinkStatus Status { get; private set; }

    [BindProperty] public string? TargetUrl { get; set; }
    [BindProperty] public DateTime? ExpiresAtUtc { get; set; }
    [BindProperty] public string? Description { get; set; }

    [TempData] public string? Message { get; set; }

    public async Task<IActionResult> OnGetAsync(long id, CancellationToken ct)
    {
        var link = await store.GetAsync(id, ct);
        if (link is null) return NotFound();
        Fill(link);
        return Page();
    }

    public async Task<IActionResult> OnPostAsync(long id, CancellationToken ct)
    {
        var link = await store.GetAsync(id, ct);
        if (link is null) return NotFound();
        LinkId = id;
        Status = link.GetStatus(DateTimeOffset.UtcNow);

        DateTimeOffset? expires = ExpiresAtUtc is { } dt ? new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)) : null;
        var error = LinkPublisher.Validate(TargetUrl, expires, Description, out var target);
        if (error is null && !await store.UpdateAsync(id, target!, expires, Description, ct))
            error = "Link can no longer be edited (used, revoked or expired).";
        if (error is not null)
        {
            ModelState.AddModelError(string.Empty, error);
            return Page();
        }
        Message = "Изменения сохранены.";
        return RedirectToPage("/Admin/Index");
    }

    private void Fill(LinkSummary link)
    {
        LinkId = link.Id;
        Status = link.GetStatus(DateTimeOffset.UtcNow);
        TargetUrl = link.TargetUrl;
        ExpiresAtUtc = link.ExpiresAt?.UtcDateTime;
        Description = link.Description;
    }
}
