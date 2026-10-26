using System.Net;
using System.Net.Http.Json;
using Microsoft.Extensions.Logging;

namespace OneTimeRedirect.IntegrationTests;

public sealed class LogSafetyTests
{
    /// <summary>Records messages, exceptions and scope state: providers such as JSON console or OpenTelemetry print scopes.</summary>
    public sealed class CaptureProvider : ILoggerProvider, ILogger
    {
        public List<string> Lines { get; } = [];
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull
        {
            var text = state is IEnumerable<KeyValuePair<string, object?>> pairs
                ? string.Join(";", pairs.Select(p => $"{p.Key}={p.Value}"))
                : state.ToString();
            lock (Lines) Lines.Add("scope: " + text);
            return null;
        }
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel l, EventId e, TState s, Exception? ex, Func<TState, Exception?, string> f)
        {
            var values = s is IEnumerable<KeyValuePair<string, object?>> pairs ? string.Join(";", pairs.Select(p => $"{p.Key}={p.Value}")) : "";
            lock (Lines) Lines.Add($"{l}: {f(s, ex)} [{values}] {ex}");
        }
        public void Dispose() { }

        public string[] Snapshot() { lock (Lines) return [.. Lines]; }
    }

    private sealed record Created(long Id, string Link);

    [Fact]
    public async Task Tokens_AreNeverLogged_EvenAtTraceLevel()
    {
        var capture = new CaptureProvider();
        using var app = new AppFactory(logProvider: capture);
        using var client = await app.CreateAdminClientAsync();
        var resp = await client.PostAsJsonAsync("/admin/api/links", new { targetUrl = "https://example.com/log" });
        var path = new Uri((await resp.Content.ReadFromJsonAsync<Created>())!.Link).AbsolutePath;
        var token = path["/r/".Length..];

        using var bot = new HttpRequestMessage(HttpMethod.Get, path);
        bot.Headers.TryAddWithoutValidation("User-Agent", "TelegramBot (like TwitterBot)");
        await client.SendAsync(bot);
        await client.SendAsync(new HttpRequestMessage(HttpMethod.Head, path));
        Assert.Equal(HttpStatusCode.Found, (await client.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(path.ToUpperInvariant())).StatusCode);

        var lines = capture.Snapshot();
        Assert.NotEmpty(lines);
        Assert.Contains(lines, l => l.StartsWith("scope: ")); // scopes are really captured
        Assert.DoesNotContain(lines, l => l.Contains(token, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(lines, l => l.Contains("RequestPath=/r/", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task StorageError_IsLoggedWithSqliteCode_ButWithoutTokenOrPath()
    {
        var capture = new CaptureProvider();
        using var app = new AppFactory(logProvider: capture);
        using var client = await app.CreateAdminClientAsync();
        var path = await AppFactory.CreateLinkPathAsync(client);
        var token = path["/r/".Length..];
        app.Exec("CREATE TRIGGER block_update BEFORE UPDATE ON RedirectLinks BEGIN SELECT RAISE(ABORT, 'disk error'); END;");

        using var failed = await client.GetAsync(path);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);

        var lines = capture.Snapshot();
        Assert.Contains(lines, l => l.StartsWith("Error: Redirect not served: SQLite error 19/"));
        Assert.DoesNotContain(lines, l => l.Contains(token, StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(lines, l => l.Contains("/r/", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task RateLimitedRequest_DoesNotLogToken()
    {
        var capture = new CaptureProvider();
        using var app = new AppFactory(logProvider: capture, publicRateLimit: 1);
        using var client = await app.CreateAdminClientAsync();
        var path = await AppFactory.CreateLinkPathAsync(client);
        var token = path["/r/".Length..];

        Assert.Equal(HttpStatusCode.Found, (await client.GetAsync(path)).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync(path)).StatusCode);

        Assert.DoesNotContain(capture.Snapshot(), l => l.Contains(token, StringComparison.OrdinalIgnoreCase));
    }
}
