using System.Security.Cryptography;

namespace OneTimeRedirect.Web;

/// <summary>PBKDF2-SHA256 password hashes in the form <c>v1.{iterations}.{saltBase64}.{keyBase64}</c>.</summary>
public static class PasswordHasher
{
    public const int DefaultIterations = 600_000;
    private const int SaltSize = 16;
    private const int KeySize = 32;

    public static string Hash(string password, int iterations = DefaultIterations)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var key = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, KeySize);
        return $"v1.{iterations}.{Convert.ToBase64String(salt)}.{Convert.ToBase64String(key)}";
    }

    public static bool IsWellFormed(string? stored) => TryParse(stored, out _, out _, out _);

    public static bool Verify(string password, string stored)
    {
        if (!TryParse(stored, out var iterations, out var salt, out var key)) return false;
        var actual = Rfc2898DeriveBytes.Pbkdf2(password, salt, iterations, HashAlgorithmName.SHA256, key.Length);
        return CryptographicOperations.FixedTimeEquals(actual, key);
    }

    private static bool TryParse(string? stored, out int iterations, out byte[] salt, out byte[] key)
    {
        iterations = 0; salt = []; key = [];
        var parts = stored?.Split('.');
        if (parts is not { Length: 4 } || parts[0] != "v1" ||
            !int.TryParse(parts[1], out iterations) || iterations < 1) return false;
        try
        {
            salt = Convert.FromBase64String(parts[2]);
            key = Convert.FromBase64String(parts[3]);
        }
        catch (FormatException) { return false; }
        return salt.Length >= 8 && key.Length >= 16;
    }
}

public sealed record AdminCredentials(string UserName, string PasswordHash);

/// <summary>
/// In-memory per-client login limiter (single instance deployment). An attempt is reserved atomically
/// before the password is checked, so parallel requests cannot all pass the check before any failure is counted.
/// </summary>
public sealed class LoginThrottle(int maxFailures, TimeSpan window)
{
    internal sealed class Entry(DateTime windowStart)
    {
        public DateTime WindowStart { get; } = windowStart;
        public int Attempts; // failures plus attempts still in flight
    }

    private readonly Dictionary<string, Entry> _entries = new();
    private readonly Lock _lock = new();

    /// <summary>Reserves one attempt, or returns null when the client is locked out.</summary>
    public LoginAttempt? TryBegin(string client)
    {
        var now = DateTime.UtcNow;
        lock (_lock)
        {
            if (!_entries.TryGetValue(client, out var e) || now - e.WindowStart >= window)
                _entries[client] = e = new Entry(now);
            if (e.Attempts >= maxFailures) return null;
            e.Attempts++;
            if (_entries.Count > 10_000)
                foreach (var (k, v) in _entries)
                    if (now - v.WindowStart >= window) _entries.Remove(k);
            return new LoginAttempt(this, client, e);
        }
    }

    // The entry identity check keeps a late release from touching a newer window of the same client.
    internal void Release(string client, Entry entry)
    {
        lock (_lock)
            if (_entries.TryGetValue(client, out var e) && e == entry && e.Attempts > 0) e.Attempts--;
    }

    internal void Reset(string client, Entry entry)
    {
        lock (_lock)
            if (_entries.TryGetValue(client, out var e) && e == entry) _entries.Remove(client);
    }
}

/// <summary>
/// A reserved login attempt. Disposed before <see cref="MarkChecked"/> it is given back (no password was tested);
/// after that it stays counted as a failure unless <see cref="Succeeded"/> is called, so errors fail closed.
/// </summary>
public sealed class LoginAttempt : IDisposable
{
    private readonly LoginThrottle _owner;
    private readonly string _client;
    private readonly LoginThrottle.Entry _entry;
    private bool _checked, _done;

    internal LoginAttempt(LoginThrottle owner, string client, LoginThrottle.Entry entry) =>
        (_owner, _client, _entry) = (owner, client, entry);

    public void MarkChecked() => _checked = true;

    public void Succeeded()
    {
        _done = true;
        _owner.Reset(_client, _entry);
    }

    public void Dispose()
    {
        if (_done) return;
        _done = true;
        if (!_checked) _owner.Release(_client, _entry);
    }
}

/// <summary>Global cap on concurrent PBKDF2 checks; waiting for a slot is bounded by a timeout.</summary>
public sealed class PasswordCheckLimiter(int maxConcurrent, TimeSpan queueTimeout)
{
    private readonly SemaphoreSlim _slots = new(maxConcurrent, maxConcurrent);

    /// <summary>Returns a slot to dispose after the check, or null when none freed up in time.</summary>
    public async Task<IDisposable?> TryEnterAsync(CancellationToken ct) =>
        await _slots.WaitAsync(queueTimeout, ct) ? new Slot(_slots) : null;

    private sealed class Slot(SemaphoreSlim slots) : IDisposable
    {
        private int _released;
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _released, 1) == 0) slots.Release();
        }
    }
}
