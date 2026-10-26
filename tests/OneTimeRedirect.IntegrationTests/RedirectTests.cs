using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;

namespace OneTimeRedirect.IntegrationTests;

public sealed class RedirectTests
{
    private const string Target = "https://example.com/landing?a=1";

    private sealed record Created(long Id, string Link);

    private static async Task<(Created Link, string Path)> CreateLinkAsync(HttpClient client, DateTimeOffset? expiresAt = null)
    {
        var resp = await client.PostAsJsonAsync("/admin/api/links",
            new { targetUrl = Target, expiresAt, description = "test" });
        Assert.Equal(HttpStatusCode.Created, resp.StatusCode);
        var created = (await resp.Content.ReadFromJsonAsync<Created>())!;
        Assert.StartsWith("https://short.test/r/", created.Link);
        return (created, new Uri(created.Link).AbsolutePath);
    }

    private static void Exec(string dbPath, string sql)
    {
        using var conn = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public async Task AdminApi_RequiresLogin()
    {
        using var app = new AppFactory();
        using var anon = app.CreateAnonymousClient();
        var resp = await anon.PostAsJsonAsync("/admin/api/links", new { targetUrl = Target });
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await anon.GetAsync("/admin/api/links")).StatusCode);
    }

    [Fact]
    public async Task AdminApi_RejectsMutationWithoutAntiforgeryToken()
    {
        using var app = new AppFactory();
        using var client = await app.CreateAdminClientAsync();
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");
        var resp = await client.PostAsJsonAsync("/admin/api/links", new { targetUrl = Target });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task Create_RejectsUnsupportedScheme()
    {
        using var app = new AppFactory();
        using var client = await app.CreateAdminClientAsync();
        var resp = await client.PostAsJsonAsync("/admin/api/links", new { targetUrl = "ftp://example.com/" });
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
    }

    [Fact]
    public async Task FirstGet_Returns302WithLocation_SecondGet404()
    {
        using var app = new AppFactory();
        using var client = await app.CreateAdminClientAsync();
        var (_, path) = await CreateLinkAsync(client);

        var first = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Found, first.StatusCode);
        Assert.Equal(new Uri(Target), first.Headers.Location);

        var second = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
        Assert.Null(second.Headers.Location);
    }

    [Fact]
    public async Task UnknownToken_Returns404()
    {
        using var app = new AppFactory();
        using var client = await app.CreateAdminClientAsync();
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/r/" + new string('A', 32))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/r/short")).StatusCode);
    }

    [Fact]
    public async Task ExpiredLink_Returns404()
    {
        using var app = new AppFactory();
        using var client = await app.CreateAdminClientAsync();
        var (link, path) = await CreateLinkAsync(client, DateTimeOffset.UtcNow.AddHours(1));
        Exec(app.DbPath, $"UPDATE RedirectLinks SET ExpiresAt = {DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeMilliseconds()} WHERE Id = {link.Id}");

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task RevokedLink_Returns404()
    {
        using var app = new AppFactory();
        using var client = await app.CreateAdminClientAsync();
        var (link, path) = await CreateLinkAsync(client);

        var revoke = await client.PostAsync($"/admin/api/links/{link.Id}/revoke", null);
        Assert.Equal(HttpStatusCode.NoContent, revoke.StatusCode);

        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task Head_DoesNotConsumeToken()
    {
        using var app = new AppFactory();
        using var client = await app.CreateAdminClientAsync();
        var (_, path) = await CreateLinkAsync(client);

        var head = await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, path));
        Assert.NotEqual(HttpStatusCode.Found, head.StatusCode);
        Assert.Null(head.Headers.Location);

        var get = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.Found, get.StatusCode);
    }

    [Fact]
    public async Task UsedLink_StaysUsedAfterRestart()
    {
        var dbPath = Path.Combine(Path.GetTempPath(), "otr-tests-" + Guid.NewGuid().ToString("N"), "test.db");
        try
        {
            string path;
            using (var app1 = new AppFactory(dbPath))
            using (var client = await app1.CreateAdminClientAsync())
            {
                (_, path) = await CreateLinkAsync(client);
                Assert.Equal(HttpStatusCode.Found, (await client.GetAsync(path)).StatusCode);
            }

            using var app2 = new AppFactory(dbPath);
            using var client2 = await app2.CreateAdminClientAsync();
            Assert.Equal(HttpStatusCode.NotFound, (await client2.GetAsync(path)).StatusCode);
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            try { Directory.Delete(Path.GetDirectoryName(dbPath)!, true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task ConcurrentGets_AtMostOneRedirect()
    {
        using var app = new AppFactory();
        using var client = await app.CreateAdminClientAsync();
        var (_, path) = await CreateLinkAsync(client);

        var start = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, 100).Select(async _ =>
        {
            await start.Task;
            using var resp = await client.GetAsync(path);
            return resp.StatusCode;
        }).ToArray();
        start.SetResult();
        var codes = await Task.WhenAll(tasks);

        Assert.Equal(1, codes.Count(c => c == HttpStatusCode.Found));
        Assert.All(codes.Where(c => c != HttpStatusCode.Found),
            c => Assert.True(c is HttpStatusCode.NotFound or HttpStatusCode.ServiceUnavailable, $"unexpected {c}"));
    }

    [Fact]
    public async Task WriteLocked_DoesNotRedirect_AndTokenSurvives()
    {
        using var app = new AppFactory();
        using var client = await app.CreateAdminClientAsync();
        var (_, path) = await CreateLinkAsync(client);

        HttpStatusCode failed;
        using (var blocker = new SqliteConnection($"Data Source={app.DbPath};Pooling=False"))
        {
            blocker.Open();
            using var begin = blocker.CreateCommand();
            begin.CommandText = "BEGIN IMMEDIATE;"; // holds the write lock; consume times out (5s)
            begin.ExecuteNonQuery();

            using var resp = await client.GetAsync(path);
            failed = resp.StatusCode;
            Assert.Null(resp.Headers.Location);
        }
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed);

        // Nothing was written, so the failed attempt did not burn the link.
        Assert.Equal(HttpStatusCode.Found, (await client.GetAsync(path)).StatusCode);
    }

    [Fact]
    public async Task WriteError_DoesNotRedirect()
    {
        using var app = new AppFactory();
        using var client = await app.CreateAdminClientAsync();
        var (_, path) = await CreateLinkAsync(client);
        Exec(app.DbPath, "CREATE TRIGGER block_update BEFORE UPDATE ON RedirectLinks BEGIN SELECT RAISE(ABORT, 'disk error'); END;");

        HttpStatusCode code;
        try
        {
            using var resp = await client.GetAsync(path);
            code = resp.StatusCode;
            Assert.Null(resp.Headers.Location);
        }
        catch (SqliteException)
        {
            // TestServer rethrows unhandled exceptions: still no redirect was issued.
            code = HttpStatusCode.InternalServerError;
        }
        Assert.NotEqual(HttpStatusCode.Found, code);
    }
}
