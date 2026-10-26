using System.Net;
using System.Net.Http.Json;
using System.Text.RegularExpressions;

namespace OneTimeRedirect.IntegrationTests;

public sealed partial class TargetSchemeTests
{
    private sealed record Created(long Id, string Link);

    private const string Browser = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    private static async Task<(HttpStatusCode Status, Created? Link)> ApiCreateAsync(HttpClient client, string target)
    {
        var resp = await client.PostAsJsonAsync("/admin/api/links", new { targetUrl = target });
        return (resp.StatusCode, resp.IsSuccessStatusCode ? await resp.Content.ReadFromJsonAsync<Created>() : null);
    }

    private static async Task<HttpResponseMessage> OpenAsync(HttpClient client, string path)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.TryAddWithoutValidation("User-Agent", Browser);
        return await client.SendAsync(req);
    }

    [Theory]
    [InlineData("http://example.com/page?q=1&r=%20x#section", "http://example.com/page?q=1&r=%20x#section")]
    [InlineData("https://example.com/a/b?x=1#frag", "https://example.com/a/b?x=1#frag")]
    [InlineData("www.ya.ru", "https://www.ya.ru")]
    [InlineData("ya.ru/path?q=1", "https://ya.ru/path?q=1")]
    public async Task FirstGetRedirectsToTheStoredUrlExactly_SecondGetIs404(string input, string expectedLocation)
    {
        using var app = new AppFactory();
        using var client = await app.CreateAdminClientAsync();
        var (status, link) = await ApiCreateAsync(client, input);
        Assert.Equal(HttpStatusCode.Created, status);
        Assert.StartsWith("https://short.test/r/", link!.Link);                // the public link is HTTPS whatever the target is
        var path = new Uri(link.Link).AbsolutePath;

        // The stored value is the normalized URL, and the redirect uses it unchanged (query and fragment included).
        Assert.Equal(expectedLocation, Stored(app.DbPath, link.Id));
        using var first = await OpenAsync(client, path);
        Assert.Equal(HttpStatusCode.Found, first.StatusCode);
        Assert.Equal(expectedLocation, first.Headers.NonValidated["Location"].ToString());

        using var second = await OpenAsync(client, path);
        Assert.Equal(HttpStatusCode.NotFound, second.StatusCode);
        Assert.False(second.Headers.Contains("Location"));
    }

    private static string Stored(string db, long id)
    {
        using var conn = OneTimeRedirect.Web.Operations.DatabaseChecks.Open(db, Microsoft.Data.Sqlite.SqliteOpenMode.ReadOnly);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = $"SELECT TargetUrl FROM RedirectLinks WHERE Id = {id}";
        return (string)cmd.ExecuteScalar()!;
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com")]
    [InlineData("http://user:pass@example.com")]
    [InlineData("http://example.com/\r\nSet-Cookie: x=1")]
    [InlineData("not a url")]
    public async Task Api_RejectsEverythingElse_AndStoresNothing(string input)
    {
        using var app = new AppFactory();
        using var client = await app.CreateAdminClientAsync();
        var (status, _) = await ApiCreateAsync(client, input);
        Assert.Equal(HttpStatusCode.BadRequest, status);
        Assert.Equal("[]", (await client.GetStringAsync("/admin/api/links")).Trim());
    }

    [Fact]
    public async Task Api_Update_AppliesTheSameRules()
    {
        using var app = new AppFactory();
        using var client = await app.CreateAdminClientAsync();
        var (_, link) = await ApiCreateAsync(client, "https://example.com/start");

        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"/admin/api/links/{link!.Id}", new { targetUrl = "http://updated.example/x?y=1#z" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await client.PutAsJsonAsync($"/admin/api/links/{link.Id}", new { targetUrl = "updated.example/no-scheme" })).StatusCode);
        Assert.Equal("https://updated.example/no-scheme", Stored(app.DbPath, link.Id));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/admin/api/links/{link.Id}", new { targetUrl = "javascript:alert(1)" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.PutAsJsonAsync($"/admin/api/links/{link.Id}", new { targetUrl = "ftp://x.example" })).StatusCode);
        Assert.Equal("https://updated.example/no-scheme", Stored(app.DbPath, link.Id));
    }

    [Fact]
    public async Task RazorPages_CreateAndEdit_AcceptHttp_AddHttpsWhenSchemeIsMissing_AndRejectBadSchemes()
    {
        using var app = new AppFactory();
        using var client = await app.CreateAdminClientAsync();
        var (_, link) = await ApiCreateAsync(client, "https://example.com/before"); // needs the API header, so before removing it
        client.DefaultRequestHeaders.Remove("X-CSRF-TOKEN");

        async Task<HttpResponseMessage> PostAsync(string url, string target)
        {
            var token = await AppFactory.GetFormTokenAsync(client, url);
            return await client.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["TargetUrl"] = target, ["__RequestVerificationToken"] = token
            }));
        }

        var page = await client.GetStringAsync("/admin/create");
        Assert.Contains("Целевой URL (HTTP или HTTPS)", page);
        Assert.DoesNotContain("только HTTPS", page);
        Assert.Contains("type=\"text\"", page);                                         // type=url would block scheme-less input in browsers

        var http = await PostAsync("/admin/create", "http://plain.example/p?q=1#f");
        Assert.Equal(HttpStatusCode.SeeOther, http.StatusCode);                          // Post/Redirect/Get
        var shown = Regex.Match(await client.GetStringAsync("/admin"), @"data-copy-text=""([^""]+)""");
        Assert.True(shown.Success);
        Assert.StartsWith("https://short.test/r/", WebUtility.HtmlDecode(shown.Groups[1].Value));
        var plainPath = new Uri(WebUtility.HtmlDecode(shown.Groups[1].Value)).AbsolutePath;
        using (var redirect = await OpenAsync(client, plainPath))
            Assert.Equal("http://plain.example/p?q=1#f", redirect.Headers.NonValidated["Location"].ToString());

        var bare = await PostAsync("/admin/create", "www.ya.ru");
        Assert.Equal(HttpStatusCode.SeeOther, bare.StatusCode);
        var bareLink = Regex.Match(await client.GetStringAsync("/admin"), @"data-copy-text=""([^""]+)""");
        using (var redirect = await OpenAsync(client, new Uri(WebUtility.HtmlDecode(bareLink.Groups[1].Value)).AbsolutePath))
            Assert.Equal("https://www.ya.ru", redirect.Headers.NonValidated["Location"].ToString());

        foreach (var bad in new[] { "javascript:alert(1)", "ftp://example.com", "https://u:p@example.com" })
        {
            var resp = await PostAsync("/admin/create", bad);
            Assert.Contains("Only absolute HTTP or HTTPS URLs", await resp.Content.ReadAsStringAsync());
        }

        // Edit
        var edit = $"/admin/links/{link!.Id}/edit";
        Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(edit, "http://after.example/x#y")).StatusCode);
        Assert.Equal("http://after.example/x#y", Stored(app.DbPath, link.Id));
        Assert.Equal(HttpStatusCode.Redirect, (await PostAsync(edit, "after2.example")).StatusCode);
        Assert.Equal("https://after2.example", Stored(app.DbPath, link.Id));
        var rejected = await PostAsync(edit, "javascript:alert(1)");
        Assert.Contains("Only absolute HTTP or HTTPS URLs", await rejected.Content.ReadAsStringAsync());
        Assert.Equal("https://after2.example", Stored(app.DbPath, link.Id));
        Assert.Contains("Целевой URL (HTTP или HTTPS)", await client.GetStringAsync(edit));
    }
}
