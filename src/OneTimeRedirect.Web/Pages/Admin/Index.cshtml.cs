using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace OneTimeRedirect.Web.Pages.Admin;

public sealed class IndexModel(LinkStore store, LinkTokenProtector protector, LinkPublisher publisher, ILogger<IndexModel> log) : PageModel
{
    public const int PageSize = 20;

    [BindProperty(SupportsGet = true)] public string? Status { get; set; }
    [BindProperty(SupportsGet = true)] public string? Q { get; set; }
    [BindProperty(SupportsGet = true)] public int P { get; set; } = 1;

    public LinkPage Result { get; private set; } = new([], 0, 1, 1);
    public DateTimeOffset Now { get; } = DateTimeOffset.UtcNow;

    /// <summary>Full public URLs of links that are active right now and whose token could be decrypted.</summary>
    public Dictionary<long, string> CopyUrls { get; } = [];

    [TempData] public string? Message { get; set; }

    private LinkStatus? ParsedStatus =>
        Enum.TryParse<LinkStatus>(Status, ignoreCase: true, out var s) && Enum.IsDefined(s) ? s : null;

    public async Task OnGetAsync(CancellationToken ct)
    {
        Result = await store.QueryAsync(ParsedStatus, Q, P, PageSize, ct);
        foreach (var link in Result.Items)
        {
            // Only links that are active at render time; used/revoked/expired never get a copyable URL.
            if (link.GetStatus(Now) != LinkStatus.Active || !Result.Tokens.TryGetValue(link.Id, out var stored)) continue;
            if (protector.TryUnprotect(stored.Encrypted, stored.Hash) is { } token)
                CopyUrls[link.Id] = publisher.BuildLink(token, Request);
            else // unreadable (lost/rotated-out keys, corruption): show "unavailable"; log the id only, never values
                log.LogError("Stored token of link {LinkId} cannot be decrypted; it is shown as unavailable for copying.", link.Id);
        }
    }

    public async Task<IActionResult> OnPostRevokeAsync(long id, CancellationToken ct)
    {
        Message = await store.RevokeAsync(id, ct) ? "Ссылка отозвана." : "Ссылку нельзя отозвать (уже использована, просрочена или отозвана).";
        return RedirectToPage("/Admin/Index", new { status = Status, q = Q, p = P });
    }

    public object PageRoute(int page) => new { status = Status, q = Q, p = page };
}
