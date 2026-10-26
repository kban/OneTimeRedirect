using System.Net;
using System.Net.Http.Json;

namespace OneTimeRedirect.IntegrationTests;

public sealed class PreviewSafetyTests
{
    private const string Target = "https://example.com/private/landing";

    private sealed record Created(long Id, string Link);

    private static async Task<string> CreateAsync(HttpClient client)
    {
        var resp = await client.PostAsJsonAsync("/admin/api/links", new { targetUrl = Target });
        return new Uri((await resp.Content.ReadFromJsonAsync<Created>())!.Link).AbsolutePath;
    }

    private static HttpRequestMessage Get(string path, string? userAgent = null, params (string, string)[] headers)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, path);
        if (userAgent is not null) req.Headers.TryAddWithoutValidation("User-Agent", userAgent);
        foreach (var (k, v) in headers) req.Headers.TryAddWithoutValidation(k, v);
        return req;
    }

    private const string Chrome = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    [Theory]
    [InlineData("TelegramBot (like TwitterBot)")]
    [InlineData("Mozilla/5.0 (compatible; Discordbot/2.0; +https://discordapp.com)")]
    [InlineData("Slackbot-LinkExpanding 1.0 (+https://api.slack.com/robots)")]
    [InlineData("WhatsApp/2.23.20.0 A")]
    [InlineData("facebookexternalhit/1.1 (+http://www.facebook.com/externalhit_uatext.php)")]
    [InlineData("Mozilla/5.0 (Macintosh) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17 Safari/605.1.15 facebookexternalhit/1.1 Facebot Twitterbot/1.0")]
    [InlineData("Mozilla/5.0 (compatible; BingPreview/1.0b)")]
    public async Task PreviewBot_GetsNoRedirect_AndDoesNotConsume(string userAgent)
    {
        using var app = new AppFactory();
        using var client = await app.CreateAdminClientAsync();
        var path = await CreateAsync(client);

        for (var i = 0; i < 3; i++) // repeated preview GETs never change state
        {
            using var preview = await client.SendAsync(Get(path, userAgent));
            Assert.Equal(HttpStatusCode.NotFound, preview.StatusCode);
            Assert.Null(preview.Headers.Location);
            Assert.DoesNotContain("example.com", await preview.Content.ReadAsStringAsync());
        }

        using var human = await client.SendAsync(Get(path, Chrome));
        Assert.Equal(HttpStatusCode.Found, human.StatusCode);
        Assert.Equal(new Uri(Target), human.Headers.Location);
    }

    [Theory]
    [InlineData("Sec-Purpose", "prefetch")]
    [InlineData("Sec-Purpose", "prefetch;anonymous-client-ip")]
    [InlineData("Purpose", "prefetch")]
    [InlineData("X-Moz", "prefetch")]
    public async Task BrowserPrefetch_DoesNotConsume(string header, string value)
    {
        using var app = new AppFactory();
        using var client = await app.CreateAdminClientAsync();
        var path = await CreateAsync(client);

        using var prefetch = await client.SendAsync(Get(path, Chrome, (header, value)));
        Assert.Equal(HttpStatusCode.NotFound, prefetch.StatusCode);
        Assert.Null(prefetch.Headers.Location);

        Assert.Equal(HttpStatusCode.Found, (await client.SendAsync(Get(path, Chrome))).StatusCode);
    }

    [Theory]
    [InlineData(Chrome)]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 17_5 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.5 Mobile/15E148 Safari/604.1")]
    [InlineData("Mozilla/5.0 (Linux; Android 14; SM-S918B) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Mobile Safari/537.36")]
    [InlineData("Mozilla/5.0 (Linux; Android 13; Pixel 7 Build/TQ3A; wv) AppleWebKit/537.36 (KHTML, like Gecko) Version/4.0 Chrome/126.0.0.0 Mobile Safari/537.36")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:128.0) Gecko/20100101 Firefox/128.0")]
    [InlineData("Mozilla/5.0 (Android 12; Mobile; rv:128.0) Gecko/128.0 Firefox/128.0")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36 Edg/126.0.0.0")]
    [InlineData("")]
    public async Task RegularBrowser_IsNotMisclassified_AndRedirectsImmediately(string userAgent)
    {
        using var app = new AppFactory();
        using var client = await app.CreateAdminClientAsync();
        var path = await CreateAsync(client);

        using var resp = await client.SendAsync(Get(path, userAgent));
        Assert.Equal(HttpStatusCode.Found, resp.StatusCode);
    }

    [Fact]
    public async Task HumanOpen_AfterPreview_StillOnlyWorksOnce()
    {
        using var app = new AppFactory();
        using var client = await app.CreateAdminClientAsync();
        var path = await CreateAsync(client);

        await client.SendAsync(Get(path, "TelegramBot (like TwitterBot)"));
        Assert.Equal(HttpStatusCode.Found, (await client.SendAsync(Get(path, Chrome))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.SendAsync(Get(path, Chrome))).StatusCode);
        // Spoofing a preview UA after use cannot reveal or revive anything.
        using var spoof = await client.SendAsync(Get(path, "TelegramBot (like TwitterBot)"));
        Assert.Equal(HttpStatusCode.NotFound, spoof.StatusCode);
        Assert.Null(spoof.Headers.Location);
    }

    [Fact]
    public async Task Responses_AreNotCacheable_NotIndexable_AndHideReferrer()
    {
        using var app = new AppFactory();
        using var client = await app.CreateAdminClientAsync();
        var path = await CreateAsync(client);

        using var resp = await client.SendAsync(Get(path, Chrome));
        Assert.Equal(HttpStatusCode.Found, resp.StatusCode);
        Assert.Contains("no-store", resp.Headers.CacheControl!.ToString());
        Assert.Equal("no-referrer", resp.Headers.GetValues("Referrer-Policy").Single());
        Assert.Contains("noindex", resp.Headers.GetValues("X-Robots-Tag").Single());

        using var robots = await client.GetAsync("/robots.txt");
        Assert.Equal(HttpStatusCode.OK, robots.StatusCode);
        Assert.Contains("Disallow: /", await robots.Content.ReadAsStringAsync());
    }
}
