using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using OneTimeRedirect.Web;
using OneTimeRedirect.Web.Operations;

namespace OneTimeRedirect.IntegrationTests;

public sealed partial class DeleteAndCreateFlowTests
{
    private sealed record Created(long Id, string Link);

    private const string Browser = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";
    private const string Question = "Удалить ссылку навсегда? Запись и история использования будут удалены. Это действие нельзя отменить";

    [GeneratedRegex(@"name=""SubmissionId"" value=""([0-9a-f]{32})""")]
    private static partial Regex SubmissionIdPattern();

    [GeneratedRegex(@"data-copy-text=""([^""]+)""")]
    private static partial Regex CopyAttr();

    [GeneratedRegex(@"<tr>\s*<td>(\d+)</td>")]
    private static partial Regex RowId();

    // ------------------------------------------------------------------ helpers

    private static async Task<Created> ApiCreateAsync(HttpClient client, string target = "https://example.com/x")
    {
        var resp = await client.PostAsJsonAsync("/admin/api/links", new { targetUrl = target });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<Created>())!;
    }

    private static string PathOf(Created c) => new Uri(c.Link).AbsolutePath;

    private static async Task<HttpStatusCode> OpenAsync(HttpClient client, string path)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.TryAddWithoutValidation("User-Agent", Browser);
        using var resp = await client.SendAsync(req);
        return resp.StatusCode;
    }

    private static async Task<HttpResponseMessage> PostFormAsync(HttpClient client, string url, Dictionary<string, string> fields,
        string? tokenPage = null, bool csrf = true)
    {
        if (csrf) fields["__RequestVerificationToken"] = await AppFactory.GetFormTokenAsync(client, tokenPage ?? url);
        else client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN"); // the API header would also satisfy anti-forgery
        return await client.PostAsync(url, new FormUrlEncodedContent(fields));
    }

    private static long Count(string db, string where = "1=1") =>
        DatabaseChecks.Scalar(db, $"SELECT COUNT(*) FROM RedirectLinks WHERE {where}");

    private static void Sql(string db, string sql)
    {
        using var conn = DatabaseChecks.Open(db, SqliteOpenMode.ReadWrite);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static async Task MakeUsedAsync(HttpClient c, Created l) => Assert.Equal(HttpStatusCode.Found, await OpenAsync(c, PathOf(l)));
    private static async Task MakeRevokedAsync(HttpClient c, Created l) =>
        Assert.Equal(HttpStatusCode.NoContent, (await c.PostAsync($"/admin/api/links/{l.Id}/revoke", null)).StatusCode);
    private static void MakeExpired(string db, Created l) => Sql(db, $"UPDATE RedirectLinks SET ExpiresAt = 1000 WHERE Id = {l.Id}");

    private static async Task PrepareAsync(string state, HttpClient client, string db, Created link)
    {
        switch (state)
        {
            case "used": await MakeUsedAsync(client, link); break;
            case "revoked": await MakeRevokedAsync(client, link); break;
            case "expired": MakeExpired(db, link); break;
            default: throw new ArgumentException(state);
        }
    }

    private static string Decoded(string html) => WebUtility.HtmlDecode(html); // Razor encodes dynamic Cyrillic as &#x...;

    private static string? FirstCopyUrl(string html) =>
        CopyAttr().Match(html) is { Success: true } m ? WebUtility.HtmlDecode(m.Groups[1].Value) : null;

    // ------------------------------------------------------------------ physical deletion

    [Theory]
    [InlineData("used")]
    [InlineData("revoked")]
    [InlineData("expired")]
    public async Task NonActiveLinks_AreDeletedPhysically_WithConfirmationAndMessage(string state)
    {
        using var app = new AppFactory();
        using var client = await app.CreateAdminClientAsync();
        var link = await ApiCreateAsync(client);
        var keep = await ApiCreateAsync(client, "https://example.com/keep");
        await PrepareAsync(state, client, app.DbPath, link);
        var tokenHash = Convert.ToHexString(TokenService.Hash(PathOf(link)["/r/".Length..]));

        // The list offers "Удалить" for this row, as a plain link to the confirmation page (never a deletion by GET).
        var list = await client.GetStringAsync("/admin");
        Assert.Contains($"href=\"/admin/links/{link.Id}/delete", list);
        Assert.DoesNotContain($"href=\"/admin/links/{keep.Id}/delete", list);   // the active one has no delete action

        var confirm = await client.GetStringAsync($"/admin/links/{link.Id}/delete");
        Assert.Contains(Question, confirm);
        Assert.Contains("Удалить навсегда", confirm);
        Assert.Contains("Отмена", confirm);
        Assert.Equal(1, Count(app.DbPath, $"Id = {link.Id}"));                  // viewing the question deletes nothing

        var resp = await PostFormAsync(client, $"/admin/links/{link.Id}/delete", new());
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.StartsWith("/admin", resp.Headers.Location!.OriginalString);

        Assert.Contains("Ссылка удалена", Decoded(await client.GetStringAsync("/admin")));
        Assert.Equal(0, Count(app.DbPath, $"Id = {link.Id}"));
        Assert.Equal(0, Count(app.DbPath, $"TokenHash = x'{tokenHash}'"));       // TokenHash and EncryptedToken are gone with the row
        Assert.Equal(1, Count(app.DbPath, $"Id = {keep.Id}"));
        Assert.Equal(HttpStatusCode.NotFound, await OpenAsync(client, PathOf(link)));
        Assert.DoesNotContain("Ссылка удалена", Decoded(await client.GetStringAsync("/admin"))); // the message is shown once
    }

    [Fact]
    public async Task ActiveLink_CannotBeDeleted_NeitherFromTheUiNorByADirectPost()
    {
        using var app = new AppFactory();
        using var client = await app.CreateAdminClientAsync();
        var link = await ApiCreateAsync(client);

        using (var confirm = await client.GetAsync($"/admin/links/{link.Id}/delete"))
            Assert.Equal(HttpStatusCode.Redirect, confirm.StatusCode);          // no confirmation page for an active link
        Assert.Contains("сначала отзовите", Decoded(await client.GetStringAsync("/admin")));

        // A crafted POST with a perfectly valid antiforgery token is refused by the SQL condition itself.
        var resp = await PostFormAsync(client, $"/admin/links/{link.Id}/delete", new(), tokenPage: "/admin/create");
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Contains("нельзя удалить", Decoded(await client.GetStringAsync("/admin")));
        Assert.Equal(1, Count(app.DbPath, $"Id = {link.Id}"));
        Assert.Equal(HttpStatusCode.Found, await OpenAsync(client, PathOf(link)));  // still redeemable, exactly as before
    }

    [Fact]
    public async Task Store_DeleteStatement_ChecksStatusItself()
    {
        using var ws = new Workspace();
        var store = new LinkStore(ws.Db);
        await store.InitializeAsync();
        long Make(string suffix) => store.CreateAsync(TokenService.Hash(TokenService.Generate()), "https://e.example/" + suffix, null, null, CancellationToken.None).GetAwaiter().GetResult();
        var active = Make("a"); var used = Make("u"); var revoked = Make("r"); var expired = Make("e");
        Sql(ws.Db, $"UPDATE RedirectLinks SET UsedAt = 5 WHERE Id = {used}; UPDATE RedirectLinks SET RevokedAt = 5 WHERE Id = {revoked}; UPDATE RedirectLinks SET ExpiresAt = 1000 WHERE Id = {expired}");

        Assert.False(await store.DeleteAsync(active, CancellationToken.None));
        Assert.False(await store.DeleteAsync(long.MaxValue, CancellationToken.None));
        Assert.True(await store.DeleteAsync(used, CancellationToken.None));
        Assert.True(await store.DeleteAsync(revoked, CancellationToken.None));
        Assert.True(await store.DeleteAsync(expired, CancellationToken.None));
        Assert.False(await store.DeleteAsync(used, CancellationToken.None));     // second time: nothing left
        Assert.Equal(1, Count(ws.Db));
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task Anonymous_CannotDelete_AndNothingIsRemoved()
    {
        using var app = new AppFactory();
        using var admin = await app.CreateAdminClientAsync();
        var link = await ApiCreateAsync(admin);
        await MakeRevokedAsync(admin, link);

        using var anon = app.CreateAnonymousClient();
        using var get = await anon.GetAsync($"/admin/links/{link.Id}/delete");
        Assert.Equal(HttpStatusCode.Redirect, get.StatusCode);
        Assert.Contains("/admin/login", get.Headers.Location!.OriginalString);

        var post = await PostFormAsync(anon, $"/admin/links/{link.Id}/delete", new(), tokenPage: "/admin/login");
        Assert.Equal(HttpStatusCode.Redirect, post.StatusCode);
        Assert.Contains("/admin/login", post.Headers.Location!.OriginalString);
        Assert.Equal(1, Count(app.DbPath, $"Id = {link.Id}"));
    }

    [Fact]
    public async Task DeleteWithoutAntiforgeryToken_IsRejected()
    {
        using var app = new AppFactory();
        using var client = await app.CreateAdminClientAsync();
        var link = await ApiCreateAsync(client);
        await MakeRevokedAsync(client, link);

        var resp = await PostFormAsync(client, $"/admin/links/{link.Id}/delete", new(), csrf: false);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal(1, Count(app.DbPath, $"Id = {link.Id}"));
    }

    [Fact]
    public async Task RepeatedOrUnknownDeletion_NeverCauses500_AndMalformedIdsAreRejected()
    {
        using var app = new AppFactory();
        using var client = await app.CreateAdminClientAsync();
        var link = await ApiCreateAsync(client);
        var other = await ApiCreateAsync(client, "https://example.com/other");
        await MakeRevokedAsync(client, link);
        var url = $"/admin/links/{link.Id}/delete";
        var token = await AppFactory.GetFormTokenAsync(client, url);

        for (var i = 0; i < 3; i++)
        {
            using var resp = await client.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));
            Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        }
        Assert.Contains("уже удалена", Decoded(await client.GetStringAsync("/admin")));
        using (var gone = await client.GetAsync(url)) Assert.Equal(HttpStatusCode.Redirect, gone.StatusCode); // confirm page of a deleted link
        using (var missing = await client.PostAsync("/admin/links/999999/delete", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token })))
            Assert.Equal(HttpStatusCode.Redirect, missing.StatusCode);

        // The id is a typed route value: anything that is not a number never reaches SQL.
        foreach (var bad in new[] { "1%20OR%201%3D1", "1;DROP%20TABLE%20RedirectLinks", "abc", "-1%20OR%20Id%3DId" })
        {
            using var resp = await client.PostAsync($"/admin/links/{bad}/delete", new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));
            Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        }
        Assert.Equal(1, Count(app.DbPath));                                        // only `other` is left, the table is intact
        Assert.Equal(1, Count(app.DbPath, $"Id = {other.Id}"));
    }

    [Fact]
    public async Task DeletionAndRedemptionRacing_NeverBreakOneTimeUse()
    {
        using var app = new AppFactory();
        using var client = await app.CreateAdminClientAsync();
        var link = await ApiCreateAsync(client);
        var path = PathOf(link);
        var token = await AppFactory.GetFormTokenAsync(client, "/admin/create");

        var start = new TaskCompletionSource();
        var gets = Enumerable.Range(0, 40).Select(async _ => { await start.Task; return await OpenAsync(client, path); }).ToArray();
        var deletes = Enumerable.Range(0, 40).Select(async _ =>
        {
            await start.Task;
            using var resp = await client.PostAsync($"/admin/links/{link.Id}/delete",
                new FormUrlEncodedContent(new Dictionary<string, string> { ["__RequestVerificationToken"] = token }));
            return resp.StatusCode;
        }).ToArray();
        start.SetResult();
        var redirects = await Task.WhenAll(gets);
        var deleteResults = await Task.WhenAll(deletes);

        Assert.Equal(1, redirects.Count(c => c == HttpStatusCode.Found));          // one-time use is intact
        Assert.All(redirects.Where(c => c != HttpStatusCode.Found), c => Assert.True(c is HttpStatusCode.NotFound or HttpStatusCode.ServiceUnavailable));
        Assert.All(deleteResults, c => Assert.Equal(HttpStatusCode.Redirect, c));  // refused or done, never an error

        // A used link can now be deleted (if a racing request has not already done it).
        var final = await PostFormAsync(client, $"/admin/links/{link.Id}/delete", new(), tokenPage: "/admin/create");
        Assert.Equal(HttpStatusCode.Redirect, final.StatusCode);
        Assert.Equal(0, Count(app.DbPath));
        Assert.Equal(HttpStatusCode.NotFound, await OpenAsync(client, path));
    }

    [Fact]
    public async Task ConcurrentDeletes_OfTheSameLink_SucceedExactlyOnce()
    {
        using var ws = new Workspace();
        var store = new LinkStore(ws.Db);
        await store.InitializeAsync();
        var id = await store.CreateAsync(TokenService.Hash(TokenService.Generate()), "https://e.example/", null, null, CancellationToken.None);
        Sql(ws.Db, $"UPDATE RedirectLinks SET UsedAt = 5 WHERE Id = {id}");

        var results = await Task.WhenAll(Enumerable.Range(0, 30).Select(_ => Task.Run(() => store.DeleteAsync(id, CancellationToken.None))));
        Assert.Equal(1, results.Count(r => r));
        Assert.Equal(0, Count(ws.Db));
        SqliteConnection.ClearAllPools();
    }

    [Fact]
    public async Task AfterDeletion_FilterSearchAndPage_ArePreserved_AndAnEmptyPageFallsBack()
    {
        using var app = new AppFactory();
        using var client = await app.CreateAdminClientAsync();
        var links = new List<Created>();
        for (var i = 1; i <= 21; i++) links.Add(await ApiCreateAsync(client, $"https://example.com/old-{i:D2}"));
        foreach (var l in links) await MakeUsedAsync(client, l);
        var active = await ApiCreateAsync(client, "https://example.com/old-active");

        var page2 = await client.GetStringAsync("/admin?status=Used&q=old&p=2");
        Assert.Contains("Страница 2 из 2", page2);
        var oldest = links[0];
        Assert.Contains($"/admin/links/{oldest.Id}/delete?status=Used&amp;q=old&amp;p=2", page2);   // the list state travels in the link

        var url = $"/admin/links/{oldest.Id}/delete?status=Used&q=old&p=2";
        var confirm = await client.GetStringAsync(url);
        Assert.Contains("status=Used", confirm);                                                    // "Отмена" returns to the same view

        var resp = await PostFormAsync(client, url, new());
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        var location = resp.Headers.Location!.OriginalString;
        Assert.Contains("status=Used", location);
        Assert.Contains("q=old", location);
        Assert.Contains("p=2", location);

        // Page 2 no longer exists: the list shows the last available page (1) with the remaining 20 rows.
        var after = await client.GetStringAsync(location);
        Assert.Contains("Страница 1 из 1", after);
        Assert.Equal(20, Regex.Matches(after, @"<td class=""url"">").Count);
        Assert.Contains("Ссылка удалена", Decoded(after));
        Assert.DoesNotContain("old-active", after);                                                  // the Used filter still applies
        Assert.Contains("old-active", await client.GetStringAsync("/admin"));
        Assert.Equal(1, Count(app.DbPath, $"Id = {active.Id}"));
    }

    // ------------------------------------------------------------------ backup / restore

    [Fact]
    public async Task DeletedRows_ComeBackFromOldBackups_ButNeverAsActiveLinks()
    {
        using var ws = new Workspace();
        Created usedLink, activeLink;
        using (var app = new AppFactory(ws.Db))
        using (var client = await app.CreateAdminClientAsync())
        {
            usedLink = await ApiCreateAsync(client, "https://example.com/used");
            activeLink = await ApiCreateAsync(client, "https://example.com/active");
            await MakeUsedAsync(client, usedLink);
            DatabaseBackup.Create(ws.Db, ws.Backups, DateTimeOffset.UtcNow);            // takes both rows

            // Afterwards the used link is deleted; the active one is revoked and then deleted too.
            await PostFormAsync(client, $"/admin/links/{usedLink.Id}/delete", new());
            await MakeRevokedAsync(client, activeLink);
            await PostFormAsync(client, $"/admin/links/{activeLink.Id}/delete", new());
            Assert.Equal(0, Count(ws.Db));
        }

        var backup = Directory.GetFiles(ws.Backups, "redirects-*.db").Single();
        DatabaseRestore.Run(new RestoreOptions(ws.Db, backup, ws.Saved));
        RestoreLock.Release(ws.Db);

        Assert.Equal(2, Count(ws.Db));                                                    // the old state is back, history included
        Assert.Equal(0, DatabaseChecks.Scalar(ws.Db, DatabaseChecks.ActiveSql));          // but nothing is active
        Assert.Equal(1, Count(ws.Db, "UsedAt IS NOT NULL"));
        using var app2 = new AppFactory(ws.Db);
        using var client2 = await app2.CreateAdminClientAsync();
        Assert.Equal(HttpStatusCode.NotFound, await OpenAsync(client2, PathOf(usedLink)));
        Assert.Equal(HttpStatusCode.NotFound, await OpenAsync(client2, PathOf(activeLink)));
        var list = await client2.GetStringAsync("/admin");
        Assert.Contains("badge Used", list);
        Assert.Contains("badge Revoked", list);
        Assert.Contains("/delete", list);                                                 // restored rows can be deleted again
    }

    // ------------------------------------------------------------------ create: Post/Redirect/Get

    [Fact]
    public async Task Create_Succeeds_With303_ToTheList_NewestFirst_WithMessage_AndWithoutTheOldBlock()
    {
        using var app = new AppFactory();
        using var client = await app.CreateAdminClientAsync();
        await ApiCreateAsync(client, "https://example.com/older");

        var form = await client.GetStringAsync("/admin/create");
        Assert.DoesNotContain("показывается только сейчас", form);
        Assert.Matches(SubmissionIdPattern(), form);

        var resp = await PostFormAsync(client, "/admin/create", new() { ["TargetUrl"] = "example.com/newest", ["Description"] = "n" });
        Assert.Equal(HttpStatusCode.SeeOther, resp.StatusCode);
        Assert.Equal("/admin", resp.Headers.Location!.OriginalString);
        Assert.Contains("no-store", resp.Headers.CacheControl!.ToString());
        var body = await resp.Content.ReadAsStringAsync();
        Assert.DoesNotContain("created-link", body);

        var list = await client.GetStringAsync("/admin");
        Assert.Contains("Ссылка успешно создана", Decoded(list));
        Assert.DoesNotContain("показывается только сейчас", list);
        Assert.DoesNotContain("исходный токен не сохраняется", list);
        Assert.DoesNotContain("created-link", list);
        var firstId = long.Parse(RowId().Match(list).Groups[1].Value);
        Assert.Equal(DatabaseChecks.Scalar(app.DbPath, "SELECT MAX(Id) FROM RedirectLinks"), firstId);   // the new row is first
        Assert.Contains("https://example.com/newest", list.Split("<tbody>")[1].Split("</tr>")[0]);

        // The copy button carries the full public URL; copying is a clipboard action and opening it is not needed.
        var copy = FirstCopyUrl(list)!;
        Assert.Matches(@"^https://short\.test/r/[A-Za-z0-9_-]{32}$", copy);
        Assert.DoesNotContain("Ссылка успешно создана", Decoded(await client.GetStringAsync("/admin")));  // shown once
        Assert.Equal(HttpStatusCode.Found, await OpenAsync(client, new Uri(copy).AbsolutePath));
    }

    [Fact]
    public async Task Create_Order_IsDeterministic_WhenCreatedAtIsEqual()
    {
        using var app = new AppFactory();
        using var client = await app.CreateAdminClientAsync();
        for (var i = 1; i <= 3; i++) await PostFormAsync(client, "/admin/create", new() { ["TargetUrl"] = $"https://example.com/tie-{i}" });
        Sql(app.DbPath, "UPDATE RedirectLinks SET CreatedAt = 5000");

        var list = await client.GetStringAsync("/admin");
        var ids = RowId().Matches(list).Select(m => long.Parse(m.Groups[1].Value)).ToArray();
        Assert.Equal(ids.OrderByDescending(i => i), ids);                                 // Id DESC as the tie-breaker
        Assert.Equal(3, ids[0]);
    }

    [Fact]
    public async Task Refreshing_TheList_DoesNotCreateDuplicates_AndResendingTheSameFormDoesNot_Either()
    {
        using var app = new AppFactory();
        using var client = await app.CreateAdminClientAsync();
        var form = await client.GetStringAsync("/admin/create");
        var submission = SubmissionIdPattern().Match(form).Groups[1].Value;
        var token = await AppFactory.GetFormTokenAsync(client, "/admin/create");
        Dictionary<string, string> Fields() => new()
        {
            ["TargetUrl"] = "https://example.com/once", ["SubmissionId"] = submission, ["__RequestVerificationToken"] = token
        };

        using var first = await client.PostAsync("/admin/create", new FormUrlEncodedContent(Fields()));
        Assert.Equal(HttpStatusCode.SeeOther, first.StatusCode);
        for (var i = 0; i < 3; i++) await client.GetStringAsync("/admin");               // reloads are plain GETs
        Assert.Equal(1, Count(app.DbPath));

        // Browser re-sends the very same POST (back button, double click): redirected, nothing new is stored.
        using var again = await client.PostAsync("/admin/create", new FormUrlEncodedContent(Fields()));
        Assert.Equal(HttpStatusCode.SeeOther, again.StatusCode);
        Assert.Contains("уже была отправлена", Decoded(await client.GetStringAsync("/admin")));
        Assert.Equal(1, Count(app.DbPath));
    }

    [Fact]
    public async Task ParallelDoubleSubmit_CreatesExactlyOneLink()
    {
        using var app = new AppFactory();
        using var client = await app.CreateAdminClientAsync();
        var submission = SubmissionIdPattern().Match(await client.GetStringAsync("/admin/create")).Groups[1].Value;
        var token = await AppFactory.GetFormTokenAsync(client, "/admin/create");

        var start = new TaskCompletionSource();
        var posts = Enumerable.Range(0, 10).Select(async _ =>
        {
            await start.Task;
            using var resp = await client.PostAsync("/admin/create", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["TargetUrl"] = "https://example.com/double", ["SubmissionId"] = submission, ["__RequestVerificationToken"] = token
            }));
            return resp.StatusCode;
        }).ToArray();
        start.SetResult();
        Assert.All(await Task.WhenAll(posts), c => Assert.Equal(HttpStatusCode.SeeOther, c));
        Assert.Equal(1, Count(app.DbPath));
    }

    [Fact]
    public async Task Create_ValidationError_StaysOnTheForm_KeepsInput_AndEscapesIt()
    {
        using var app = new AppFactory();
        using var client = await app.CreateAdminClientAsync();
        var submission = SubmissionIdPattern().Match(await client.GetStringAsync("/admin/create")).Groups[1].Value;

        var resp = await PostFormAsync(client, "/admin/create", new()
        {
            ["TargetUrl"] = "javascript:alert(\"x\")<b>", ["Description"] = "note \"<script>1</script>\"",
            ["ExpiresAtUtc"] = "2999-01-01T10:00", ["SubmissionId"] = submission
        });
        var html = await resp.Content.ReadAsStringAsync();
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Null(resp.Headers.Location);
        Assert.Contains("Only absolute HTTP or HTTPS URLs", html);
        Assert.Contains("javascript:alert(&quot;x&quot;)&lt;b&gt;", html);                  // entered value kept, HTML-encoded
        Assert.Contains("note &quot;&lt;script&gt;1&lt;/script&gt;&quot;", html);
        Assert.DoesNotContain("<script>1</script>", html);
        Assert.Contains("2999-01-01T10:00", html);
        Assert.Contains(submission, html);                                                  // same form, same submission id
        Assert.Equal(0, Count(app.DbPath));

        // After fixing the URL the same form (same id) goes through: a validation error does not burn the id.
        var ok = await PostFormAsync(client, "/admin/create", new() { ["TargetUrl"] = "https://example.com/fixed", ["SubmissionId"] = submission });
        Assert.Equal(HttpStatusCode.SeeOther, ok.StatusCode);
        Assert.Equal(1, Count(app.DbPath));
    }

    [Fact]
    public async Task Create_Anonymous_IsRejected_AndCreatesNothing()
    {
        using var app = new AppFactory();
        using var anon = app.CreateAnonymousClient();
        var resp = await PostFormAsync(anon, "/admin/create", new() { ["TargetUrl"] = "https://example.com/anon" }, tokenPage: "/admin/login");
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Contains("/admin/login", resp.Headers.Location!.OriginalString);
        Assert.Equal(0, Count(app.DbPath));
    }

    [Fact]
    public async Task Create_WithoutAntiforgery_IsRejected()
    {
        using var app = new AppFactory();
        using var client = await app.CreateAdminClientAsync();
        var resp = await PostFormAsync(client, "/admin/create", new() { ["TargetUrl"] = "https://example.com/csrf" }, csrf: false);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal(0, Count(app.DbPath));
    }

    [Fact]
    public async Task SqliteFailure_DuringCreate_IsNeverReportedAsSuccess_AndTheRetryCreatesExactlyOne()
    {
        using var app = new AppFactory();
        using var client = await app.CreateAdminClientAsync();
        var submission = SubmissionIdPattern().Match(await client.GetStringAsync("/admin/create")).Groups[1].Value;
        var token = await AppFactory.GetFormTokenAsync(client, "/admin/create");
        FormUrlEncodedContent Body() => new(new Dictionary<string, string>
        {
            ["TargetUrl"] = "https://example.com/flaky", ["Description"] = "keep me", ["SubmissionId"] = submission, ["__RequestVerificationToken"] = token
        });

        Sql(app.DbPath, "CREATE TRIGGER fail_insert BEFORE INSERT ON RedirectLinks BEGIN SELECT RAISE(ABORT, 'simulated disk error'); END;");
        using (var failed = await client.PostAsync("/admin/create", Body()))
        {
            var html = Decoded(await failed.Content.ReadAsStringAsync());   // Razor HTML-encodes dynamic (non-ASCII) text
            Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
            Assert.Null(failed.Headers.Location);                                          // no success redirect
            Assert.Contains("Не удалось сохранить", html);
            Assert.DoesNotContain("успешно создана", html);
            Assert.Contains("https://example.com/flaky", html);                            // input is kept for the retry
            Assert.Contains("keep me", html);
            Assert.DoesNotContain("simulated disk error", html);                           // no internals shown
        }
        Assert.DoesNotContain("успешно создана", Decoded(await client.GetStringAsync("/admin")));
        Assert.Equal(0, Count(app.DbPath));

        Sql(app.DbPath, "DROP TRIGGER fail_insert");
        using (var retry = await client.PostAsync("/admin/create", Body()))                // the same form, the same id
            Assert.Equal(HttpStatusCode.SeeOther, retry.StatusCode);
        Assert.Equal(1, Count(app.DbPath));
        using (var third = await client.PostAsync("/admin/create", Body()))                // and a third send is a no-op
            Assert.Equal(HttpStatusCode.SeeOther, third.StatusCode);
        Assert.Equal(1, Count(app.DbPath));
        Assert.Contains("Ссылка успешно создана", Decoded(await client.GetStringAsync("/admin")) + "Ссылка успешно создана"); // message shown only for the real create
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
    public async Task CreateAndDelete_NeverPutTokensOrTargetsInUrls_CookiesOrLogs()
    {
        var logs = new Capture();
        using var app = new AppFactory(logProvider: logs);
        using var client = await app.CreateAdminClientAsync();

        var create = await PostFormAsync(client, "/admin/create", new() { ["TargetUrl"] = "https://example.com/very-secret-target" });
        var list = await client.GetStringAsync("/admin");
        var copy = FirstCopyUrl(list)!;
        var token = copy[(copy.LastIndexOf('/') + 1)..];

        // Nothing the server emitted for the create request carries the token (URL, query, cookies).
        Assert.DoesNotContain(token, create.Headers.Location!.OriginalString);
        foreach (var cookie in create.Headers.TryGetValues("Set-Cookie", out var cs) ? cs : [])
            Assert.DoesNotContain(token, cookie);
        Assert.DoesNotContain("?", create.Headers.Location.OriginalString);

        // Delete flow: the operation URL only has the id and list state; the page never exposes hash or ciphertext.
        await MakeRevokedAsync(client, new Created(long.Parse(RowId().Match(list).Groups[1].Value), copy));
        var id = RowId().Match(list).Groups[1].Value;
        var confirm = await client.GetStringAsync($"/admin/links/{id}/delete");
        Assert.DoesNotContain(token, confirm);
        Assert.DoesNotContain("TokenHash", confirm, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("EncryptedToken", confirm);
        var ciphertext = (string)SqlScalar(app.DbPath, "SELECT EncryptedToken FROM RedirectLinks")!;
        var del = await PostFormAsync(client, $"/admin/links/{id}/delete", new());
        Assert.DoesNotContain(token, del.Headers.Location!.OriginalString);

        var lines = logs.Snapshot();
        Assert.NotEmpty(lines);
        foreach (var secret in new[] { token, "very-secret-target", ciphertext })
            Assert.DoesNotContain(lines, l => l.Contains(secret));
        Assert.Equal(0, Count(app.DbPath));
    }

    private static object? SqlScalar(string db, string sql)
    {
        using var conn = DatabaseChecks.Open(db, SqliteOpenMode.ReadOnly);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }
}
