using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;

namespace OneTimeRedirect.IntegrationTests;

public sealed class AdminPagesTests
{
    private sealed record Created(long Id, string Link);

    private static async Task<Created> ApiCreateAsync(HttpClient client, string target = "https://example.com/a", string? description = null)
    {
        var resp = await client.PostAsJsonAsync("/admin/api/links", new { targetUrl = target, description });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        return (await resp.Content.ReadFromJsonAsync<Created>())!;
    }

    private static async Task<HttpResponseMessage> PostFormAsync(HttpClient client, string url, string tokenPage,
        Dictionary<string, string> fields, bool withToken = true)
    {
        if (withToken) fields["__RequestVerificationToken"] = await AppFactory.GetFormTokenAsync(client, tokenPage);
        else client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN"); // the API header would also satisfy anti-forgery
        return await client.PostAsync(url, new FormUrlEncodedContent(fields));
    }

    [Theory]
    [InlineData("/admin")]
    [InlineData("/admin/create")]
    [InlineData("/admin/links/1/edit")]
    public async Task Anonymous_IsRedirectedToLogin(string url)
    {
        using var app = new AppFactory();
        using var client = app.CreateAnonymousClient();
        var resp = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.StartsWith("/admin/login", resp.Headers.Location!.OriginalString.Replace("https://localhost", ""));
    }

    [Fact]
    public async Task Anonymous_CannotPostToAdminPages()
    {
        using var app = new AppFactory();
        using var client = app.CreateAnonymousClient();
        var resp = await PostFormAsync(client, "/admin/create", "/admin/login",
            new() { ["TargetUrl"] = "https://example.com/" });
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Contains("/admin/login", resp.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Login_Success_SetsSecureHttpOnlyStrictCookie_AndGrantsAccess()
    {
        using var app = new AppFactory();
        using var client = app.CreateAnonymousClient();
        var resp = await AppFactory.LoginAsync(client, AppFactory.AdminUser, AppFactory.AdminPassword);
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Equal("/admin", resp.Headers.Location!.OriginalString);

        var cookie = resp.Headers.GetValues("Set-Cookie").Single(c => c.StartsWith("__Host-otr-admin="));
        Assert.Contains("secure", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("httponly", cookie, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("samesite=strict", cookie, StringComparison.OrdinalIgnoreCase);

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/admin")).StatusCode);
    }

    [Theory]
    [InlineData(AppFactory.AdminUser, "wrong-password-xxxxxxxx")]
    [InlineData("someone-else", AppFactory.AdminPassword)]
    public async Task Login_Failure_IsRejected(string user, string password)
    {
        using var app = new AppFactory();
        using var client = app.CreateAnonymousClient();
        var resp = await AppFactory.LoginAsync(client, user, password);
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.False(resp.Headers.Contains("Set-Cookie") && resp.Headers.GetValues("Set-Cookie").Any(c => c.StartsWith("__Host-otr-admin=")));
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/admin")).StatusCode);
    }

    [Fact]
    public async Task Login_WithoutAntiforgeryToken_IsRejected()
    {
        using var app = new AppFactory();
        using var client = app.CreateAnonymousClient();
        var resp = await PostFormAsync(client, "/admin/login", "/admin/login",
            new() { ["Username"] = AppFactory.AdminUser, ["Password"] = AppFactory.AdminPassword }, withToken: false);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Login_IsThrottledAfterRepeatedFailures_EvenWithCorrectPassword()
    {
        using var app = new AppFactory(maxLoginFailures: 3);
        using var client = app.CreateAnonymousClient();
        for (var i = 0; i < 3; i++)
            Assert.Equal(HttpStatusCode.Unauthorized, (await AppFactory.LoginAsync(client, AppFactory.AdminUser, "bad-password-" + i)).StatusCode);

        var resp = await AppFactory.LoginAsync(client, AppFactory.AdminUser, AppFactory.AdminPassword);
        Assert.Equal(HttpStatusCode.TooManyRequests, resp.StatusCode);
    }

    [Theory]
    [InlineData("https://evil.example/", "/admin")]
    [InlineData("//evil.example/", "/admin")]
    [InlineData("/admin/create", "/admin/create")]
    public async Task Login_ReturnUrl_IsOnlyHonoredForLocalPaths(string returnUrl, string expected)
    {
        using var app = new AppFactory();
        using var client = app.CreateAnonymousClient();
        var resp = await AppFactory.LoginAsync(client, AppFactory.AdminUser, AppFactory.AdminPassword, returnUrl);
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Equal(expected, resp.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task Logout_EndsSession()
    {
        using var app = new AppFactory();
        using var client = await app.CreateAdminClientAsync();
        var resp = await PostFormAsync(client, "/admin/logout", "/admin", new());
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/admin")).StatusCode);
    }

    [Fact]
    public async Task Create_RedirectsToList_ShowsLinkThere_AndNeverPutsTheTokenInTheUrl()
    {
        using var app = new AppFactory();
        using var client = await app.CreateAdminClientAsync();
        var resp = await PostFormAsync(client, "/admin/create", "/admin/create", new()
        {
            ["TargetUrl"] = "https://example.com/created", ["Description"] = "from page"
        });
        Assert.Equal(HttpStatusCode.SeeOther, resp.StatusCode);
        Assert.Equal("/admin", resp.Headers.Location!.OriginalString);
        var html = await client.GetStringAsync("/admin");
        var link = Regex.Match(html, @"data-copy-text=""([^""]+)""");
        Assert.True(link.Success, "created link not shown in the list");
        var path = new Uri(WebUtility.HtmlDecode(link.Groups[1].Value)).AbsolutePath;
        var token = path["/r/".Length..];
        Assert.DoesNotContain(token, resp.Headers.Location.OriginalString);

        // The raw token is shown in the authenticated admin list (decrypted on demand) but never in the JSON API.
        Assert.DoesNotContain(token, await client.GetStringAsync("/admin/api/links"));
        foreach (var file in new[] { app.DbPath, app.DbPath + "-wal" }.Where(File.Exists))
        {
            await using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(fs, System.Text.Encoding.Latin1);
            Assert.DoesNotContain(token, await reader.ReadToEndAsync());
        }

        Assert.Equal(HttpStatusCode.Found, (await client.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task Create_InvalidUrl_ShowsError_AndCreatesNothing()
    {
        using var app = new AppFactory();
        using var client = await app.CreateAdminClientAsync();
        var resp = await PostFormAsync(client, "/admin/create", "/admin/create", new() { ["TargetUrl"] = "ftp://files.example/" });
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("Only absolute HTTP or HTTPS URLs", await resp.Content.ReadAsStringAsync());
        Assert.Equal("[]", (await client.GetStringAsync("/admin/api/links")).Trim());
    }

    [Fact]
    public async Task Create_WithoutAntiforgeryToken_IsRejected_AndCreatesNothing()
    {
        using var app = new AppFactory();
        using var client = await app.CreateAdminClientAsync();
        var resp = await PostFormAsync(client, "/admin/create", "/admin/create",
            new() { ["TargetUrl"] = "https://example.com/csrf" }, withToken: false);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal("[]", (await client.GetStringAsync("/admin/api/links")).Trim());
    }

    [Fact]
    public async Task Revoke_WithoutAntiforgeryToken_IsRejected()
    {
        using var app = new AppFactory();
        using var client = await app.CreateAdminClientAsync();
        var created = await ApiCreateAsync(client);
        var resp = await PostFormAsync(client, "/admin?handler=Revoke", "/admin", new() { ["id"] = created.Id.ToString() }, withToken: false);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        Assert.Equal(HttpStatusCode.Found, (await client.GetAsync(new Uri(created.Link).AbsolutePath)).StatusCode);
    }

    [Fact]
    public async Task Edit_ActiveLink_Succeeds()
    {
        using var app = new AppFactory();
        using var client = await app.CreateAdminClientAsync();
        var created = await ApiCreateAsync(client);
        var url = $"/admin/links/{created.Id}/edit";

        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync(url)).StatusCode);
        var resp = await PostFormAsync(client, url, url, new() { ["TargetUrl"] = "https://example.org/changed", ["Description"] = "edited" });
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);

        var redirect = await client.GetAsync(new Uri(created.Link).AbsolutePath);
        Assert.Equal(new Uri("https://example.org/changed"), redirect.Headers.Location);
    }

    [Fact]
    public async Task Edit_UsedLink_IsForbidden()
    {
        using var app = new AppFactory();
        using var client = await app.CreateAdminClientAsync();
        var created = await ApiCreateAsync(client, "https://example.com/original");
        Assert.Equal(HttpStatusCode.Found, (await client.GetAsync(new Uri(created.Link).AbsolutePath)).StatusCode);
        var url = $"/admin/links/{created.Id}/edit";

        var page = await client.GetStringAsync(url);
        Assert.Contains("Редактирование недоступно", page);
        Assert.DoesNotContain("name=\"TargetUrl\"", page);

        // A crafted POST (token taken from the logout form) must not change the used link either.
        var token = await AppFactory.GetFormTokenAsync(client, url);
        var resp = await client.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["TargetUrl"] = "https://evil.example/", ["__RequestVerificationToken"] = token
        }));
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Contains("Редактирование недоступно", await resp.Content.ReadAsStringAsync());
        Assert.Contains("https://example.com/original", await client.GetStringAsync("/admin"));
        Assert.DoesNotContain("evil.example", await client.GetStringAsync("/admin"));
    }

    [Fact]
    public async Task Revoke_FromPage_MakesLinkUnusable()
    {
        using var app = new AppFactory();
        using var client = await app.CreateAdminClientAsync();
        var created = await ApiCreateAsync(client);

        var resp = await PostFormAsync(client, "/admin?handler=Revoke", "/admin", new() { ["id"] = created.Id.ToString() });
        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(new Uri(created.Link).AbsolutePath)).StatusCode);
        Assert.Contains("Revoked", await client.GetStringAsync("/admin"));
    }

    [Fact]
    public async Task List_ShowsAllFourStatuses_AndFiltersByStatus()
    {
        using var app = new AppFactory();
        using var client = await app.CreateAdminClientAsync();
        var active = await ApiCreateAsync(client, "https://example.com/active");
        var used = await ApiCreateAsync(client, "https://example.com/used");
        var revoked = await ApiCreateAsync(client, "https://example.com/revoked");
        var expired = await ApiCreateAsync(client, "https://example.com/expired");
        await client.GetAsync(new Uri(used.Link).AbsolutePath);
        await client.PostAsync($"/admin/api/links/{revoked.Id}/revoke", null);
        using (var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={app.DbPath};Pooling=False"))
        {
            conn.Open();
            using var cmd = conn.CreateCommand();
            cmd.CommandText = $"UPDATE RedirectLinks SET ExpiresAt = 1000 WHERE Id = {expired.Id}";
            cmd.ExecuteNonQuery();
        }

        var all = await client.GetStringAsync("/admin");
        foreach (var s in new[] { "Active", "Used", "Expired", "Revoked" })
            Assert.Contains($"badge {s}", all);

        var onlyUsed = await client.GetStringAsync("/admin?status=Used");
        Assert.Contains("example.com/used", onlyUsed);
        Assert.DoesNotContain("example.com/active", onlyUsed);
        Assert.DoesNotContain("example.com/revoked", onlyUsed);

        var search = await client.GetStringAsync("/admin?q=expired");
        Assert.Contains("example.com/expired", search);
        Assert.DoesNotContain("example.com/active", search);
    }

    [Fact]
    public async Task List_IsPaginatedOnTheServer()
    {
        using var app = new AppFactory();
        using var client = await app.CreateAdminClientAsync();
        for (var i = 1; i <= 25; i++) await ApiCreateAsync(client, $"https://example.com/item-{i:D2}");

        var page1 = await client.GetStringAsync("/admin");
        var page2 = await client.GetStringAsync("/admin?p=2");
        Assert.Equal(20, RowCount(page1));
        Assert.Equal(5, RowCount(page2));
        Assert.Contains("Страница 1 из 2", page1);
        Assert.Contains("Страница 2 из 2", page2);
        // Newest first: the oldest item lands on the last page only.
        Assert.Contains("item-25", page1); Assert.DoesNotContain("item-01", page1);
        Assert.Contains("item-01", page2); Assert.DoesNotContain("item-25", page2);

        // Out-of-range page is clamped instead of failing.
        Assert.Equal(5, RowCount(await client.GetStringAsync("/admin?p=999")));
    }

    private static int RowCount(string html) => Regex.Matches(html, @"<td class=""url"">").Count;
}
