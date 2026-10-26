using Microsoft.Data.Sqlite;

namespace OneTimeRedirect.Web;

public sealed class LinkStore(string databasePath)
{
    private string ConnectionString { get; } = BuildConnectionString(databasePath);

    private static string BuildConnectionString(string path)
    {
        var absolute = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
        return new SqliteConnectionStringBuilder
        {
            DataSource = absolute,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true,
            DefaultTimeout = 5
        }.ToString();
    }

    internal async Task<SqliteConnection> OpenAsync(CancellationToken ct)
    {
        var conn = new SqliteConnection(ConnectionString);
        try
        {
            await conn.OpenAsync(ct);
            // Per-connection setting: in WAL mode NORMAL can lose the last commits on power loss,
            // which would bring a used link back to life. FULL syncs the WAL on every commit.
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA synchronous=FULL;";
            await cmd.ExecuteNonQueryAsync(ct);
            return conn;
        }
        catch
        {
            await conn.DisposeAsync();
            throw;
        }
    }

    public async Task InitializeAsync()
    {
        await using var conn = await OpenAsync(CancellationToken.None);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            CREATE TABLE IF NOT EXISTS RedirectLinks (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                TokenHash BLOB NOT NULL UNIQUE,
                TargetUrl TEXT NOT NULL,
                Description TEXT NULL,
                CreatedAt INTEGER NOT NULL,
                ExpiresAt INTEGER NULL,
                UsedAt INTEGER NULL,
                RevokedAt INTEGER NULL,
                EncryptedToken TEXT NULL
            );
            CREATE INDEX IF NOT EXISTS IX_RedirectLinks_CreatedAt ON RedirectLinks(CreatedAt);
            """;
        await cmd.ExecuteNonQueryAsync();

        // Additive migration of databases created before EncryptedToken existed: existing rows keep all their data
        // and get NULL (those links can never be shown again, their raw token was never stored).
        var hasColumn = false;
        await using (var info = conn.CreateCommand())
        {
            info.CommandText = "PRAGMA table_info(RedirectLinks);";
            await using var reader = await info.ExecuteReaderAsync();
            while (await reader.ReadAsync()) hasColumn |= reader.GetString(1) == "EncryptedToken";
        }
        if (!hasColumn)
        {
            await using var alter = conn.CreateCommand();
            alter.CommandText = "ALTER TABLE RedirectLinks ADD COLUMN EncryptedToken TEXT NULL;";
            try { await alter.ExecuteNonQueryAsync(); }
            catch (SqliteException ex) when (ex.Message.Contains("duplicate column", StringComparison.OrdinalIgnoreCase)) { } // another instance won the race
        }
    }

    public async Task<long> CreateAsync(byte[] hash, string target, DateTimeOffset? expires, string? description,
        CancellationToken ct, string? encryptedToken = null)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT INTO RedirectLinks (TokenHash, TargetUrl, Description, CreatedAt, ExpiresAt, EncryptedToken)
            VALUES ($hash, $target, $desc, $now, $expires, $enc)
            RETURNING Id;
            """;
        cmd.Parameters.AddWithValue("$enc", (object?)encryptedToken ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$hash", hash);
        cmd.Parameters.AddWithValue("$target", target);
        cmd.Parameters.AddWithValue("$desc", (object?)description ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$expires", (object?)expires?.ToUnixTimeMilliseconds() ?? DBNull.Value);
        return (long)(await cmd.ExecuteScalarAsync(ct))!;
    }

    public async Task<string?> ConsumeAsync(byte[] hash, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE RedirectLinks SET UsedAt = $now
            WHERE TokenHash = $hash
              AND UsedAt IS NULL AND RevokedAt IS NULL
              AND (ExpiresAt IS NULL OR ExpiresAt > $now)
            RETURNING TargetUrl;
            """;
        cmd.Parameters.AddWithValue("$hash", hash);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        return (string?)await cmd.ExecuteScalarAsync(ct);
    }

    public async Task<bool> RevokeAsync(long id, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE RedirectLinks SET RevokedAt = $now
            WHERE Id = $id AND UsedAt IS NULL AND RevokedAt IS NULL
              AND (ExpiresAt IS NULL OR ExpiresAt > $now);
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    /// <summary>
    /// Permanently removes a link (row, TokenHash and EncryptedToken). Only used, revoked or expired links qualify:
    /// the condition lives in the DELETE itself, so an active link can never be deleted, whatever the caller does,
    /// and a concurrent redemption cannot be raced (redemption needs the row to be unused).
    /// </summary>
    public async Task<bool> DeleteAsync(long id, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM RedirectLinks
            WHERE Id = $id
              AND (UsedAt IS NOT NULL
                   OR RevokedAt IS NOT NULL
                   OR (ExpiresAt IS NOT NULL AND ExpiresAt <= $now))
            RETURNING Id;
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        return await cmd.ExecuteScalarAsync(ct) is not null;
    }

    public async Task<bool> UpdateAsync(long id, string target, DateTimeOffset? expires, string? description, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            UPDATE RedirectLinks
            SET TargetUrl = $target, ExpiresAt = $expires, Description = $desc
            WHERE Id = $id AND UsedAt IS NULL AND RevokedAt IS NULL
              AND (ExpiresAt IS NULL OR ExpiresAt > $now);
            """;
        cmd.Parameters.AddWithValue("$id", id);
        cmd.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$target", target);
        cmd.Parameters.AddWithValue("$expires", (object?)expires?.ToUnixTimeMilliseconds() ?? DBNull.Value);
        cmd.Parameters.AddWithValue("$desc", (object?)description ?? DBNull.Value);
        return await cmd.ExecuteNonQueryAsync(ct) == 1;
    }

    public async Task<List<LinkSummary>> ListAsync(CancellationToken ct)
    {
        var result = new List<LinkSummary>();
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT Id, TargetUrl, Description, CreatedAt, ExpiresAt, UsedAt, RevokedAt
            FROM RedirectLinks ORDER BY CreatedAt DESC LIMIT 100;
            """;
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct)) result.Add(ReadSummary(reader));
        return result;
    }

    private const string SummaryColumns = "Id, TargetUrl, Description, CreatedAt, ExpiresAt, UsedAt, RevokedAt";

    private static LinkSummary ReadSummary(SqliteDataReader reader) => new(
        reader.GetInt64(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2),
        DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)),
        reader.IsDBNull(4) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)),
        reader.IsDBNull(5) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(5)),
        reader.IsDBNull(6) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6)));

    public async Task<LinkSummary?> GetAsync(long id, CancellationToken ct)
    {
        await using var conn = await OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT {SummaryColumns} FROM RedirectLinks WHERE Id = $id;";
        cmd.Parameters.AddWithValue("$id", id);
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct) ? ReadSummary(reader) : null;
    }

    /// <summary>Server-side filtered page; page is 1-based and clamped to the last page.</summary>
    public async Task<LinkPage> QueryAsync(LinkStatus? status, string? search, int page, int pageSize, CancellationToken ct)
    {
        var where = new List<string>();
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        where.Add(status switch
        {
            LinkStatus.Used => "UsedAt IS NOT NULL",
            LinkStatus.Revoked => "UsedAt IS NULL AND RevokedAt IS NOT NULL",
            LinkStatus.Expired => "UsedAt IS NULL AND RevokedAt IS NULL AND ExpiresAt IS NOT NULL AND ExpiresAt <= $now",
            LinkStatus.Active => "UsedAt IS NULL AND RevokedAt IS NULL AND (ExpiresAt IS NULL OR ExpiresAt > $now)",
            _ => "1=1"
        });
        if (!string.IsNullOrWhiteSpace(search))
            where.Add("(TargetUrl LIKE $q ESCAPE '\\' OR Description LIKE $q ESCAPE '\\')");
        var clause = string.Join(" AND ", where);
        var like = "%" + (search ?? "").Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

        await using var conn = await OpenAsync(ct);
        await using var count = conn.CreateCommand();
        count.CommandText = $"SELECT COUNT(*) FROM RedirectLinks WHERE {clause};";
        count.Parameters.AddWithValue("$now", now);
        count.Parameters.AddWithValue("$q", like);
        var total = (long)(await count.ExecuteScalarAsync(ct))!;

        var pages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));
        page = Math.Clamp(page, 1, pages);

        await using var cmd = conn.CreateCommand();
        // The encrypted token and its hash are read only for currently active rows; for every other row the
        // database never hands the ciphertext to the application.
        const string active = "UsedAt IS NULL AND RevokedAt IS NULL AND (ExpiresAt IS NULL OR ExpiresAt > $now)";
        cmd.CommandText = $"""
            SELECT {SummaryColumns},
                   CASE WHEN {active} THEN EncryptedToken END, CASE WHEN {active} THEN TokenHash END
            FROM RedirectLinks WHERE {clause}
            ORDER BY CreatedAt DESC, Id DESC LIMIT $limit OFFSET $offset;
            """;
        cmd.Parameters.AddWithValue("$now", now);
        cmd.Parameters.AddWithValue("$q", like);
        cmd.Parameters.AddWithValue("$limit", pageSize);
        cmd.Parameters.AddWithValue("$offset", (page - 1) * pageSize);
        var items = new List<LinkSummary>();
        var stored = new Dictionary<long, StoredToken>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var summary = ReadSummary(reader);
            items.Add(summary);
            if (!reader.IsDBNull(7) && !reader.IsDBNull(8))
                stored[summary.Id] = new StoredToken(reader.GetString(7), (byte[])reader.GetValue(8));
        }
        return new LinkPage(items, total, page, pages, stored);
    }
}

public enum LinkStatus { Active, Used, Expired, Revoked }

/// <summary>Ciphertext of the raw token plus its hash (to verify the ciphertext belongs to the row). Never serialized.</summary>
public sealed record StoredToken(string Encrypted, byte[] Hash);

public sealed record LinkPage(List<LinkSummary> Items, long Total, int Page, int TotalPages,
    IReadOnlyDictionary<long, StoredToken>? StoredTokens = null)
{
    public IReadOnlyDictionary<long, StoredToken> Tokens => StoredTokens ?? new Dictionary<long, StoredToken>();
}

public sealed record LinkSummary(long Id, string TargetUrl, string? Description,
    DateTimeOffset CreatedAt, DateTimeOffset? ExpiresAt, DateTimeOffset? UsedAt, DateTimeOffset? RevokedAt)
{
    public LinkStatus GetStatus(DateTimeOffset now) =>
        UsedAt is not null ? LinkStatus.Used
        : RevokedAt is not null ? LinkStatus.Revoked
        : ExpiresAt is not null && ExpiresAt <= now ? LinkStatus.Expired
        : LinkStatus.Active;
}
