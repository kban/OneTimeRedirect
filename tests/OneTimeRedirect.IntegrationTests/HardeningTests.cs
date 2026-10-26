using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using OneTimeRedirect.Web;

namespace OneTimeRedirect.IntegrationTests;

/// <summary>Storage failures, durability, public rate limit and production configuration.</summary>
public sealed class HardeningTests
{
    [Fact]
    public async Task CantOpenDatabase_Returns503_WithoutRedirect_AndTokenSurvives()
    {
        using var app = new AppFactory();
        using var client = await app.CreateAdminClientAsync();
        var path = await AppFactory.CreateLinkPathAsync(client);

        // Move the database away and put a directory in its place: every open fails with SQLITE_CANTOPEN (14).
        SqliteConnection.ClearAllPools();
        var moved = app.DbPath + ".moved";
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            if (File.Exists(app.DbPath + suffix)) File.Move(app.DbPath + suffix, moved + suffix);
        Directory.CreateDirectory(app.DbPath);

        using (var failed = await client.GetAsync(path))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
            Assert.Null(failed.Headers.Location);
            Assert.NotNull(failed.Headers.RetryAfter);
        }

        SqliteConnection.ClearAllPools();
        Directory.Delete(app.DbPath);
        foreach (var suffix in new[] { "", "-wal", "-shm" })
            if (File.Exists(moved + suffix)) File.Move(moved + suffix, app.DbPath + suffix);
        Assert.Equal(HttpStatusCode.Found, (await client.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task WriteError_Returns503_AndDoesNotMarkUsed()
    {
        using var app = new AppFactory();
        using var client = await app.CreateAdminClientAsync();
        var path = await AppFactory.CreateLinkPathAsync(client);
        app.Exec("CREATE TRIGGER block_update BEFORE UPDATE ON RedirectLinks BEGIN SELECT RAISE(ABORT, 'disk error'); END;");

        using var resp = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, resp.StatusCode);
        Assert.Null(resp.Headers.Location);
        Assert.Equal(0L, app.Scalar("SELECT COUNT(*) FROM RedirectLinks WHERE UsedAt IS NOT NULL"));
    }

    [Fact]
    public async Task Connections_UseSynchronousFull()
    {
        using var app = new AppFactory();
        var store = app.Services.GetRequiredService<LinkStore>();
        for (var i = 0; i < 3; i++) // pooled connections too
        {
            await using var conn = await store.OpenAsync(CancellationToken.None);
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA synchronous;";
            Assert.Equal(2L, await cmd.ExecuteScalarAsync()); // 2 = FULL
        }
    }

    [Fact]
    public async Task PublicRateLimit_Returns429_WithoutConsumingTheLink()
    {
        using var app = new AppFactory(publicRateLimit: 2);
        using var client = await app.CreateAdminClientAsync();
        var path = await AppFactory.CreateLinkPathAsync(client);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/r/" + new string('a', 32))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, path))).StatusCode);
        using var limited = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Null(limited.Headers.Location);
        Assert.Equal(0L, app.Scalar("SELECT COUNT(*) FROM RedirectLinks WHERE UsedAt IS NOT NULL"));

        // The admin area is not affected by the public limit.
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/admin")).StatusCode);
    }

    [Fact]
    public async Task Csp_ForbidsBaseElement()
    {
        using var app = new AppFactory();
        using var client = app.CreateAnonymousClient();
        using var resp = await client.GetAsync("/admin/login");
        Assert.Contains("base-uri 'none'", resp.Headers.GetValues("Content-Security-Policy").Single());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("http://short.test")]
    [InlineData("https://short.test/prefix")]
    [InlineData("https://short.test/?a=1")]
    [InlineData("not a url")]
    public void Production_RequiresValidPublicBaseUrl(string? publicBaseUrl)
    {
        using var app = new AppFactory(environment: "Production", publicBaseUrl: publicBaseUrl);
        var ex = Assert.ThrowsAny<Exception>(() => app.CreateAnonymousClient("https://short.test"));
        Assert.Contains("PUBLIC_BASE_URL", Flatten(ex));
    }

    [Fact]
    public void InvalidPublicBaseUrl_IsRejectedInDevelopmentToo()
    {
        using var app = new AppFactory(publicBaseUrl: "http://short.test");
        var ex = Assert.ThrowsAny<Exception>(() => app.CreateAnonymousClient());
        Assert.Contains("PUBLIC_BASE_URL", Flatten(ex));
    }

    [Fact]
    public async Task Production_AllowsOnlyThePublicHost()
    {
        using var app = new AppFactory(environment: "Production", publicBaseUrl: "https://short.test/");
        using var client = app.CreateAnonymousClient("https://short.test");
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/health")).StatusCode);

        using var foreign = new HttpRequestMessage(HttpMethod.Get, "/health");
        foreign.Headers.Host = "attacker.example";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SendAsync(foreign)).StatusCode);

        // Links are built from PUBLIC_BASE_URL (origin only), not from the request.
        var login = await AppFactory.LoginAsync(client, AppFactory.AdminUser, AppFactory.AdminPassword);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        var csrf = await client.GetFromJsonAsync<Dictionary<string, string>>("/admin/api/csrf");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf!["token"]);
        var created = await client.PostAsJsonAsync("/admin/api/links", new { targetUrl = "https://example.com/p" });
        var link = (await created.Content.ReadFromJsonAsync<Dictionary<string, object>>())!["link"].ToString();
        Assert.Matches("^https://short\\.test/r/[A-Za-z0-9_-]{32}$", link);
    }

    private static string Flatten(Exception ex)
    {
        var text = "";
        for (Exception? e = ex; e is not null; e = e.InnerException) text += e.Message + "\n";
        return text;
    }
}
