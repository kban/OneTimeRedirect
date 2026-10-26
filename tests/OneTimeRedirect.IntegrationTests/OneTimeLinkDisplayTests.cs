using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using OneTimeRedirect.Web;
using OneTimeRedirect.Web.Operations;

namespace OneTimeRedirect.IntegrationTests;

public sealed partial class OneTimeLinkDisplayTests
{
    private sealed record Created(long Id, string Link);

    private const string Unavailable = "Недоступна для копирования";
    private const string Invalid = "Недействительна";
    private const string Browser = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    private static async Task<Created> CreateAsync(HttpClient client, string target = "https://example.com/x")
    {
        var resp = await client.PostAsJsonAsync("/admin/api/links", new { targetUrl = target });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<Created>())!;
    }

    private static string PathOf(Created c) => new Uri(c.Link).AbsolutePath;
    private static string TokenOf(Created c) => PathOf(c)["/r/".Length..];

    private static async Task<HttpStatusCode> OpenAsync(HttpClient client, string path)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.TryAddWithoutValidation("User-Agent", Browser);
        using var resp = await client.SendAsync(req);
        return resp.StatusCode;
    }

    private static void Sql(string db, string sql)
    {
        using var conn = DatabaseChecks.Open(db, SqliteOpenMode.ReadWrite);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    /// <summary>Table rows of /admin keyed by link id.</summary>
    private static Dictionary<long, string> Rows(string html) =>
        RowPattern().Matches(html).ToDictionary(m => long.Parse(m.Groups[1].Value), m => m.Groups[2].Value);

    private static string? CopyUrl(string row) =>
        CopyAttr().Match(row) is { Success: true } m ? WebUtility.HtmlDecode(m.Groups[1].Value) : null;

    [GeneratedRegex(@"<tr>\s*<td>(\d+)</td>(.*?)</tr>", RegexOptions.Singleline)]
    private static partial Regex RowPattern();

    [GeneratedRegex(@"data-copy-text=""([^""]*)""")]
    private static partial Regex CopyAttr();

    // ---------------------------------------------------------------- display

    [Fact]
    public async Task ActiveLink_ShowsFullPublicUrl_BuiltFromPublicBaseUrl_AndCanBeCopiedRepeatedly()
    {
        using var app = new AppFactory();
        using var client = await app.CreateAdminClientAsync();
        var created = await CreateAsync(client);

        var first = await client.GetStringAsync("/admin");
        var second = await client.GetStringAsync("/admin");

        Assert.Contains("Одноразовая ссылка", first);
        Assert.Equal(created.Link, CopyUrl(Rows(first)[created.Id]));
        Assert.StartsWith("https://short.test/r/", created.Link);          // PUBLIC_BASE_URL + token, always HTTPS
        Assert.Equal(CopyUrl(Rows(first)[created.Id]), CopyUrl(Rows(second)[created.Id])); // same value every time
        Assert.Contains("Копировать", Rows(first)[created.Id]);
        // The visible text is the full URL too (CSS shortens it visually; the clipboard value is the data attribute).
        Assert.Contains($"<code>{created.Link}</code>", Rows(first)[created.Id]);
    }

    [Fact]
    public async Task OneTimeColumn_FollowsTargetUrlColumn()
    {
        using var app = new AppFactory();
        using var client = await app.CreateAdminClientAsync();
        var html = await client.GetStringAsync("/admin");   // header is rendered even with a row present
        await CreateAsync(client);
        html = await client.GetStringAsync("/admin");
        var target = html.IndexOf("<th>Целевой URL</th>", StringComparison.Ordinal);
        var column = html.IndexOf("<th>Одноразовая ссылка</th>", StringComparison.Ordinal);
        var description = html.IndexOf("Описание</th>", StringComparison.Ordinal);
        Assert.True(target > 0 && target < column && column < description);
    }

    [Fact]
    public async Task TokenIsNeverPlacedInAttributesThatLoadResources_OrInQueryStrings()
    {
        using var app = new AppFactory();
        using var client = await app.CreateAdminClientAsync();
        var created = await CreateAsync(client);
        var token = TokenOf(created);
        var html = await client.GetStringAsync("/admin");

        // Only data-copy-text (inert) and the visible <code> text may carry the token.
        foreach (Match m in Regex.Matches(html, @"\b(href|src|action|formaction|srcset|poster|data)=""[^""]*""", RegexOptions.IgnoreCase))
            Assert.DoesNotContain(token, m.Value);
        Assert.DoesNotContain("<a ", Regex.Match(html, @"<td class=""otl"">.*?</td>", RegexOptions.Singleline).Value); // not even a link element
        Assert.DoesNotContain(token, string.Join("\n", Regex.Matches(html, @"/admin[^""\s]*\?[^""\s]*").Select(m => m.Value)));
    }

    [Fact]
    public async Task AdminResponses_AreNoStore_AndCopyScriptNeverTouchesTheNetworkOrStorage()
    {
        using var app = new AppFactory();
        using var client = await app.CreateAdminClientAsync();
        await CreateAsync(client);

        using var resp = await client.GetAsync("/admin");
        Assert.Contains("no-store", resp.Headers.CacheControl!.ToString());

        var js = await client.GetStringAsync("/js/admin.js");
        foreach (var forbidden in new[] { "fetch(", "XMLHttpRequest", "sendBeacon", "localStorage", "sessionStorage", "document.cookie", "new Image", "WebSocket", "EventSource", "window.open", "location" })
            Assert.DoesNotContain(forbidden, js);
        Assert.Contains("navigator.clipboard.writeText", js);
    }

    [Fact]
    public async Task UsedLink_IsNoLongerCopyable_EvenThoughCiphertextRemainsInTheDatabase()
    {
        using var app = new AppFactory();
        using var client = await app.CreateAdminClientAsync();
        var created = await CreateAsync(client);
        Assert.Equal(HttpStatusCode.Found, await OpenAsync(client, PathOf(created)));

        var html = await client.GetStringAsync("/admin");
        Assert.DoesNotContain(TokenOf(created), html);
        Assert.Contains(Invalid, Rows(html)[created.Id]);
        Assert.Null(CopyUrl(Rows(html)[created.Id]));
        Assert.Equal(1, DatabaseChecks.Scalar(app.DbPath, "SELECT COUNT(*) FROM RedirectLinks WHERE UsedAt IS NOT NULL AND EncryptedToken IS NOT NULL"));
    }

    [Fact]
    public async Task RevokedLink_IsNoLongerCopyable()
    {
        using var app = new AppFactory();
        using var client = await app.CreateAdminClientAsync();
        var created = await CreateAsync(client);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/admin/api/links/{created.Id}/revoke", null)).StatusCode);

        var html = await client.GetStringAsync("/admin");
        Assert.DoesNotContain(TokenOf(created), html);
        Assert.Contains(Invalid, Rows(html)[created.Id]);
    }

    [Fact]
    public async Task ExpiredLink_IsNoLongerCopyable()
    {
        using var app = new AppFactory();
        using var client = await app.CreateAdminClientAsync();
        var created = await CreateAsync(client);
        Sql(app.DbPath, $"UPDATE RedirectLinks SET ExpiresAt = 1000 WHERE Id = {created.Id}");

        var html = await client.GetStringAsync("/admin");
        Assert.DoesNotContain(TokenOf(created), html);
        Assert.Contains(Invalid, Rows(html)[created.Id]);
    }

    [Fact]
    public async Task FilteredAndPagedViews_KeepWorkingWithTheNewColumn()
    {
        using var app = new AppFactory();
        using var client = await app.CreateAdminClientAsync();
        var created = new List<Created>();
        for (var i = 1; i <= 25; i++) created.Add(await CreateAsync(client, $"https://example.com/item-{i:D2}"));
        Assert.Equal(HttpStatusCode.Found, await OpenAsync(client, PathOf(created[0])));

        var page2 = Rows(await client.GetStringAsync("/admin?p=2"));
        Assert.Equal(5, page2.Count);
        Assert.Contains(Invalid, page2[created[0].Id]);                    // oldest, used
        Assert.Equal(created[1].Link, CopyUrl(page2[created[1].Id]));

        var active = Rows(await client.GetStringAsync("/admin?status=Active"));
        Assert.All(active.Values, r => Assert.NotNull(CopyUrl(r)));
        var used = Rows(await client.GetStringAsync("/admin?status=Used"));
        Assert.Single(used);
        Assert.All(used.Values, r => Assert.Null(CopyUrl(r)));
    }

    // ---------------------------------------------------------------- access control and secrecy

    [Fact]
    public async Task Anonymous_NeverReceivesTokens()
    {
        using var app = new AppFactory();
        using var admin = await app.CreateAdminClientAsync();
        var created = await CreateAsync(admin);
        var token = TokenOf(created);

        using var anon = app.CreateAnonymousClient();
        foreach (var url in new[] { "/admin", "/admin?status=Active", "/admin/api/links", "/admin/api/csrf", "/admin/create", $"/admin/links/{created.Id}/edit" })
        {
            using var resp = await anon.GetAsync(url);
            Assert.True(resp.StatusCode is HttpStatusCode.Redirect or HttpStatusCode.Unauthorized, $"{url} -> {resp.StatusCode}");
            Assert.DoesNotContain(token, await resp.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task AdminApi_NeverExposesEncryptedOrRawTokens()
    {
        using var app = new AppFactory();
        using var client = await app.CreateAdminClientAsync();
        var created = await CreateAsync(client);
        var json = await client.GetStringAsync("/admin/api/links");
        var ciphertext = (string)Scalar(app.DbPath, "SELECT EncryptedToken FROM RedirectLinks")!;

        Assert.DoesNotContain(TokenOf(created), json);
        Assert.DoesNotContain(ciphertext, json);
        Assert.DoesNotContain("ncrypted", json);
        Assert.DoesNotContain("tokenHash", json, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(TokenOf(created), ciphertext);
        Assert.DoesNotContain(TokenOf(created), ciphertext);
    }

    private static object? Scalar(string db, string sql)
    {
        using var conn = DatabaseChecks.Open(db, SqliteOpenMode.ReadOnly);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private sealed class Capture : ILoggerProvider, ILogger
    {
        public List<string> Lines { get; } = [];
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel l, EventId e, TState s, Exception? ex, Func<TState, Exception?, string> f)
        {
            lock (Lines) Lines.Add(f(s, ex) + ex);
        }
        public void Dispose() { }
        public string[] Snapshot() { lock (Lines) return [.. Lines]; }
    }

    [Fact]
    public async Task Tokens_AndCiphertext_NeverReachTheLogs_EvenWhenDecryptionFails()
    {
        var logs = new Capture();
        using var app = new AppFactory(logProvider: logs);
        using var client = await app.CreateAdminClientAsync();
        var good = await CreateAsync(client);
        var bad = await CreateAsync(client);
        Sql(app.DbPath, $"UPDATE RedirectLinks SET EncryptedToken = 'CfDJ8-corrupted-ciphertext-AAAA' WHERE Id = {bad.Id}");

        var html = await client.GetStringAsync("/admin");
        Assert.Equal(good.Link, CopyUrl(Rows(html)[good.Id]));
        Assert.Contains(Unavailable, Rows(html)[bad.Id]);
        await client.GetStringAsync("/admin?status=Active");

        var lines = logs.Snapshot();
        Assert.Contains(lines, l => l.Contains($"link {bad.Id}") || l.Contains(bad.Id.ToString()) && l.Contains("cannot be decrypted"));
        foreach (var secret in new[] { TokenOf(good), TokenOf(bad), "CfDJ8-corrupted-ciphertext-AAAA", (string)Scalar(app.DbPath, $"SELECT EncryptedToken FROM RedirectLinks WHERE Id = {good.Id}")! })
            Assert.DoesNotContain(lines, l => l.Contains(secret));
    }

    // ---------------------------------------------------------------- robustness

    [Fact]
    public async Task LegacyRowsWithoutEncryptedToken_AreHandled_AndStillRedeemable()
    {
        using var app = new AppFactory();
        using var client = await app.CreateAdminClientAsync();
        var token = TokenService.Generate();
        var hash = Convert.ToHexString(TokenService.Hash(token));
        Sql(app.DbPath, $"INSERT INTO RedirectLinks (TokenHash, TargetUrl, Description, CreatedAt) VALUES (x'{hash}', 'https://legacy.example/', 'old', 1)");
        var id = (long)Scalar(app.DbPath, "SELECT Id FROM RedirectLinks WHERE Description = 'old'")!;

        var html = await client.GetStringAsync("/admin");
        Assert.Contains(Unavailable, Rows(html)[id]);
        Assert.Null(CopyUrl(Rows(html)[id]));
        Assert.Equal(HttpStatusCode.Found, await OpenAsync(client, "/r/" + token)); // existing redemption logic unchanged
        Assert.Equal(HttpStatusCode.NotFound, await OpenAsync(client, "/r/" + token));
    }

    [Fact]
    public async Task CorruptOrSwappedCiphertext_DoesNotBreakThePage_OrOtherRows()
    {
        using var app = new AppFactory();
        using var client = await app.CreateAdminClientAsync();
        var a = await CreateAsync(client, "https://example.com/a");
        var b = await CreateAsync(client, "https://example.com/b");
        var c = await CreateAsync(client, "https://example.com/c");
        var d = await CreateAsync(client, "https://example.com/d");
        Sql(app.DbPath, $"UPDATE RedirectLinks SET EncryptedToken = 'not-base64-!!' WHERE Id = {a.Id}");
        Sql(app.DbPath, $"UPDATE RedirectLinks SET EncryptedToken = (SELECT EncryptedToken FROM RedirectLinks WHERE Id = {d.Id}) WHERE Id = {c.Id}"); // valid ciphertext, wrong row
        Sql(app.DbPath, $"UPDATE RedirectLinks SET EncryptedToken = '' WHERE Id = {b.Id}");

        using var resp = await client.GetAsync("/admin");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var rows = Rows(await resp.Content.ReadAsStringAsync());
        Assert.Contains(Unavailable, rows[a.Id]);
        Assert.Contains(Unavailable, rows[b.Id]);
        Assert.Contains(Unavailable, rows[c.Id]);          // a ciphertext that belongs to another row is refused
        Assert.DoesNotContain(TokenOf(d), rows[c.Id]);
        Assert.Equal(d.Link, CopyUrl(rows[d.Id]));
        Assert.DoesNotContain("Exception", await client.GetStringAsync("/admin"));
        // The unusable ciphertext has no effect on redemption.
        Assert.Equal(HttpStatusCode.Found, await OpenAsync(client, PathOf(a)));
    }

    [Fact]
    public async Task ActiveLinksSurviveRestart_WhenTheSameKeyRingIsKept_AndDegradeGracefullyWhenKeysAreLost()
    {
        using var ws = new Workspace();
        Created first;
        using (var app1 = new AppFactory(ws.Db))
        using (var client1 = await app1.CreateAdminClientAsync())
            first = await CreateAsync(client1);

        using (var app2 = new AppFactory(ws.Db))
        using (var client2 = await app2.CreateAdminClientAsync())
        {
            var rows = Rows(await client2.GetStringAsync("/admin"));
            Assert.Equal(first.Link, CopyUrl(rows[first.Id]));                       // restart, same keys
        }

        SqliteConnection.ClearAllPools();
        Directory.Delete(Path.Combine(Path.GetDirectoryName(ws.Db)!, "keys"), recursive: true); // key ring lost
        using var app3 = new AppFactory(ws.Db);
        using var client3 = await app3.CreateAdminClientAsync();
        using var resp = await client3.GetAsync("/admin");
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains(Unavailable, Rows(await resp.Content.ReadAsStringAsync())[first.Id]);
        Assert.Equal(HttpStatusCode.Found, await OpenAsync(client3, PathOf(first)));  // the link itself still works
    }

    // ---------------------------------------------------------------- schema migration

    private const string LegacySchema = """
        CREATE TABLE RedirectLinks (
            Id INTEGER PRIMARY KEY AUTOINCREMENT,
            TokenHash BLOB NOT NULL UNIQUE,
            TargetUrl TEXT NOT NULL,
            Description TEXT NULL,
            CreatedAt INTEGER NOT NULL,
            ExpiresAt INTEGER NULL,
            UsedAt INTEGER NULL,
            RevokedAt INTEGER NULL
        );
        CREATE INDEX IX_RedirectLinks_CreatedAt ON RedirectLinks(CreatedAt);
        """;

    /// <summary>A database as written by the previous release: active, used, revoked and expired rows, no EncryptedToken.</summary>
    private static (string Active, string Used, string Revoked, string Expired) BuildLegacyDatabase(string db)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(db)!);
        string T() => TokenService.Generate();
        var (a, u, r, e) = (T(), T(), T(), T());
        string H(string t) => Convert.ToHexString(TokenService.Hash(t));
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        using (var conn = DatabaseChecks.Open(db, SqliteOpenMode.ReadWriteCreate))
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "PRAGMA journal_mode=WAL;" + LegacySchema + $"""
                INSERT INTO RedirectLinks (TokenHash, TargetUrl, Description, CreatedAt, ExpiresAt, UsedAt, RevokedAt) VALUES
                 (x'{H(a)}', 'https://legacy.example/active', 'legacy-active', {now}, NULL, NULL, NULL),
                 (x'{H(u)}', 'https://legacy.example/used', 'legacy-used', {now - 5000}, NULL, 777777, NULL),
                 (x'{H(r)}', 'https://legacy.example/revoked', 'legacy-revoked', {now - 4000}, NULL, NULL, 888888),
                 (x'{H(e)}', 'https://legacy.example/expired', 'legacy-expired', {now - 3000}, 1000, NULL, NULL);
                """;
            cmd.ExecuteNonQuery();
        }
        SqliteConnection.ClearAllPools();
        return (a, u, r, e);
    }

    [Fact]
    public async Task Migration_AddsTheColumn_KeepsEveryRowAndStatus_AndLegacyLinksStayRedeemable()
    {
        using var ws = new Workspace();
        var (active, used, revoked, expired) = BuildLegacyDatabase(ws.Db);

        using var app = new AppFactory(ws.Db);                      // starting the app runs the migration
        using var client = await app.CreateAdminClientAsync();
        var html = await client.GetStringAsync("/admin");

        Assert.Equal(1, DatabaseChecks.Scalar(ws.Db, "SELECT COUNT(*) FROM pragma_table_info('RedirectLinks') WHERE name = 'EncryptedToken'"));
        Assert.Equal(4, DatabaseChecks.Scalar(ws.Db, DatabaseChecks.TotalSql));
        Assert.Equal(0, DatabaseChecks.Scalar(ws.Db, "SELECT COUNT(*) FROM RedirectLinks WHERE EncryptedToken IS NOT NULL"));
        Assert.Equal(777777, DatabaseChecks.Scalar(ws.Db, "SELECT UsedAt FROM RedirectLinks WHERE Description = 'legacy-used'"));
        Assert.Equal(888888, DatabaseChecks.Scalar(ws.Db, "SELECT RevokedAt FROM RedirectLinks WHERE Description = 'legacy-revoked'"));
        Assert.Equal(1000, DatabaseChecks.Scalar(ws.Db, "SELECT ExpiresAt FROM RedirectLinks WHERE Description = 'legacy-expired'"));
        foreach (var status in new[] { "Active", "Used", "Revoked", "Expired" })
            Assert.Contains($"badge {status}", html);
        Assert.Contains(Unavailable, html);                          // the legacy active link
        Assert.Equal(4, Rows(html).Count);

        Assert.Equal(HttpStatusCode.Found, await OpenAsync(client, "/r/" + active));
        Assert.Equal(HttpStatusCode.NotFound, await OpenAsync(client, "/r/" + active));
        foreach (var dead in new[] { used, revoked, expired })
            Assert.Equal(HttpStatusCode.NotFound, await OpenAsync(client, "/r/" + dead));

        // New links created after the migration are copyable next to the legacy ones.
        var fresh = await CreateAsync(client);
        Assert.Equal(fresh.Link, CopyUrl(Rows(await client.GetStringAsync("/admin"))[fresh.Id]));
    }

    [Fact]
    public async Task Migration_IsIdempotent_AcrossRestarts()
    {
        using var ws = new Workspace();
        BuildLegacyDatabase(ws.Db);
        for (var i = 0; i < 3; i++)
        {
            using var app = new AppFactory(ws.Db);
            using var client = await app.CreateAdminClientAsync();
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/admin")).StatusCode);
        }
        Assert.Equal(1, DatabaseChecks.Scalar(ws.Db, "SELECT COUNT(*) FROM pragma_table_info('RedirectLinks') WHERE name = 'EncryptedToken'"));
        Assert.Equal(4, DatabaseChecks.Scalar(ws.Db, DatabaseChecks.TotalSql));
    }

    // ---------------------------------------------------------------- backup / restore with the new schema

    [Fact]
    public async Task BackupOfALegacySchemaDatabase_Restores_AndTheAppMigratesItAfterwards()
    {
        using var ws = new Workspace();
        BuildLegacyDatabase(ws.Db);
        var backup = DatabaseBackup.Create(ws.Db, ws.Backups, DateTimeOffset.UtcNow);   // old schema is still a valid backup
        File.Delete(ws.Db);

        var result = DatabaseRestore.Run(new RestoreOptions(ws.Db, backup.Path, ws.Saved));
        RestoreLock.Release(ws.Db);
        Assert.Equal(2, result.Revoked);                                                // legacy-active and legacy-expired
        Assert.Equal(0, DatabaseChecks.Scalar(ws.Db, DatabaseChecks.ActiveSql));

        using var app = new AppFactory(ws.Db);
        using var client = await app.CreateAdminClientAsync();
        var html = await client.GetStringAsync("/admin");
        Assert.DoesNotContain("badge Active", html);
        Assert.Equal(1, DatabaseChecks.Scalar(ws.Db, "SELECT COUNT(*) FROM pragma_table_info('RedirectLinks') WHERE name = 'EncryptedToken'"));
    }

    [Fact]
    public async Task BackupOfTheNewSchema_RoundTrips_AndRestoredLinksNeverShowACopyUrl()
    {
        using var ws = new Workspace();
        Created created;
        BackupResult backup;
        using (var app1 = new AppFactory(ws.Db))
        using (var client1 = await app1.CreateAdminClientAsync())
        {
            created = await CreateAsync(client1);
            backup = DatabaseBackup.Create(ws.Db, ws.Backups, DateTimeOffset.UtcNow);   // live WAL, new schema
        }
        Assert.Null(DatabaseChecks.Verify(backup.Path));
        Assert.Equal(1, DatabaseChecks.Scalar(backup.Path, "SELECT COUNT(*) FROM RedirectLinks WHERE EncryptedToken IS NOT NULL"));

        DatabaseRestore.Run(new RestoreOptions(ws.Db, backup.Path, ws.Saved));
        RestoreLock.Release(ws.Db);

        using var app2 = new AppFactory(ws.Db);
        using var client2 = await app2.CreateAdminClientAsync();
        var html = await client2.GetStringAsync("/admin");
        Assert.DoesNotContain(TokenOf(created), html);
        Assert.Contains(Invalid, Rows(html)[created.Id]);                                // restored links are revoked, so not copyable
        Assert.Equal(HttpStatusCode.NotFound, await OpenAsync(client2, PathOf(created)));
    }

    // ---------------------------------------------------------------- the protector itself

    [Fact]
    public void Protector_RoundTrips_BindsToTheRow_AndUsesItsOwnPurpose()
    {
        var provider = new EphemeralDataProtectionProvider();
        var protector = new LinkTokenProtector(provider);
        var token = TokenService.Generate();
        var hash = TokenService.Hash(token);

        var cipher = protector.Protect(token);
        Assert.DoesNotContain(token, cipher);
        Assert.Equal(token, protector.TryUnprotect(cipher, hash));
        Assert.NotEqual(cipher, protector.Protect(token));                               // randomized
        Assert.Null(protector.TryUnprotect(cipher, TokenService.Hash(TokenService.Generate()))); // other row's hash
        Assert.Null(protector.TryUnprotect("garbage", hash));
        Assert.Null(protector.TryUnprotect("", hash));
        Assert.Null(protector.TryUnprotect(cipher[..^3] + "AAA", hash));                 // tampered

        // The same key ring under a different purpose cannot read these values.
        var foreign = provider.CreateProtector("Some.Other.Purpose").Protect(token);
        Assert.Null(protector.TryUnprotect(foreign, hash));
        Assert.Equal("OneTimeRedirect.LinkTokens.v1", LinkTokenProtector.Purpose);
        // A different key ring cannot read them either.
        Assert.Null(new LinkTokenProtector(new EphemeralDataProtectionProvider()).TryUnprotect(cipher, hash));
    }
}
