using System.Text.RegularExpressions;

namespace OneTimeRedirect.Web;

/// <summary>
/// Idempotency for the create form: every rendered form carries a random submission id and the first POST that
/// reserves it wins, so a double click or a re-sent POST cannot create two links. The id is released when the attempt
/// fails (for example a database error), which keeps a later retry of the same form possible. In memory only: a restart
/// forgets ids, which is acceptable for a single-instance admin tool (a restart also drops the browser's open form).
/// </summary>
public sealed partial class SubmissionGuard
{
    private static readonly TimeSpan Lifetime = TimeSpan.FromHours(2);
    private readonly Dictionary<string, DateTime> _ids = new();
    private readonly Lock _lock = new();

    [GeneratedRegex("^[0-9a-f]{32}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdFormat();

    public static string NewId() => Guid.NewGuid().ToString("N");

    public static bool IsValid(string? id) => id is not null && IdFormat().IsMatch(id);

    /// <summary>True for the first caller; false when the id is already reserved or used.</summary>
    public bool TryReserve(string id)
    {
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            if (_ids.Count > 10_000)
                foreach (var (k, at) in _ids.ToArray())
                    if (now - at > Lifetime) _ids.Remove(k);
            if (_ids.TryGetValue(id, out var existing) && now - existing <= Lifetime) return false;
            _ids[id] = now;
            return true;
        }
    }

    /// <summary>Gives the id back after a failed attempt so the same form can be submitted again.</summary>
    public void Release(string id)
    {
        lock (_lock) _ids.Remove(id);
    }
}
