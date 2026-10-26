using System.Net.Http.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using OneTimeRedirect.Web;

namespace OneTimeRedirect.IntegrationTests;

/// <summary>Application bound to its own temporary SQLite database.</summary>
public sealed partial class AppFactory : WebApplicationFactory<Program>
{
    public const string AdminUser = "test-admin";
    public const string AdminPassword = "test-only-password-0123456789";

    // Low iteration count keeps tests fast; production hashes use PasswordHasher.DefaultIterations.
    private static readonly string AdminHash = PasswordHasher.Hash(AdminPassword, iterations: 1_000);

    private readonly bool _ownsDirectory;
    private readonly int _maxLoginFailures;
    private readonly ILoggerProvider? _logProvider;
    private readonly int _publicRateLimit;
    private readonly int _maxPasswordChecks;
    private readonly string _environment;
    private readonly string? _publicBaseUrl;

    public string DbPath { get; }

    /// <param name="publicRateLimit">High by default so bulk tests are not throttled.</param>
    /// <param name="publicBaseUrl">null leaves PUBLIC_BASE_URL unset.</param>
    public AppFactory(string? dbPath = null, int maxLoginFailures = 1000, ILoggerProvider? logProvider = null,
        int publicRateLimit = 100_000, int maxPasswordChecks = 2, string environment = "Development",
        string? publicBaseUrl = "https://short.test")
    {
        _ownsDirectory = dbPath is null;
        _maxLoginFailures = maxLoginFailures;
        _logProvider = logProvider;
        _publicRateLimit = publicRateLimit;
        _maxPasswordChecks = maxPasswordChecks;
        _environment = environment;
        _publicBaseUrl = publicBaseUrl;
        DbPath = dbPath ?? Path.Combine(Path.GetTempPath(), "otr-tests-" + Guid.NewGuid().ToString("N"), "test.db");
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment(_environment);
        // UseSetting is visible to WebApplication.CreateBuilder in Program.cs (ConfigureAppConfiguration is not).
        builder.UseSetting("REDIRECT_DB_PATH", DbPath);
        builder.UseSetting("REDIRECT_ADMIN_PASSWORD_HASH", AdminHash);
        builder.UseSetting("REDIRECT_ADMIN_USER", AdminUser);
        builder.UseSetting("ADMIN_LOGIN_MAX_FAILURES", _maxLoginFailures.ToString());
        builder.UseSetting("ADMIN_LOGIN_MAX_CONCURRENT_CHECKS", _maxPasswordChecks.ToString());
        builder.UseSetting("PUBLIC_RATE_LIMIT_PER_MINUTE", _publicRateLimit.ToString());
        if (_publicBaseUrl is not null) builder.UseSetting("PUBLIC_BASE_URL", _publicBaseUrl);
        if (_logProvider is not null) // raise levels via config, like an operator debugging in production
        {
            builder.UseSetting("Logging:LogLevel:Default", "Trace");
            builder.UseSetting("Logging:LogLevel:Microsoft.AspNetCore", "Trace");
            builder.ConfigureLogging(l => { l.SetMinimumLevel(LogLevel.Trace); l.AddProvider(_logProvider); });
        }
    }

    /// <summary>Cookie-keeping client that does not follow redirects; https because the auth cookie is Secure.</summary>
    public HttpClient CreateAnonymousClient(string baseAddress = "https://localhost") => CreateClient(new WebApplicationFactoryClientOptions
    {
        AllowAutoRedirect = false,
        BaseAddress = new Uri(baseAddress),
        HandleCookies = true
    });

    /// <summary>Creates a link through the admin API and returns its /r/{token} path.</summary>
    public static async Task<string> CreateLinkPathAsync(HttpClient adminClient, string target = "https://example.com/t")
    {
        var resp = await adminClient.PostAsJsonAsync("/admin/api/links", new { targetUrl = target });
        resp.EnsureSuccessStatusCode();
        return new Uri((await resp.Content.ReadFromJsonAsync<LinkResponse>())!.Link).AbsolutePath;
    }

    public void Exec(string sql)
    {
        using var conn = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    public object? Scalar(string sql)
    {
        using var conn = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }

    private sealed record LinkResponse(long Id, string Link);

    /// <summary>Anonymous client logged in as the admin; the anti-forgery header for the JSON API is preset.</summary>
    public async Task<HttpClient> CreateAdminClientAsync()
    {
        var client = CreateAnonymousClient();
        var resp = await LoginAsync(client, AdminUser, AdminPassword);
        if (resp.StatusCode != System.Net.HttpStatusCode.Redirect)
            throw new InvalidOperationException($"Login failed: {(int)resp.StatusCode}");
        var csrf = await client.GetFromJsonAsync<CsrfResponse>("/admin/api/csrf");
        client.DefaultRequestHeaders.Add("X-CSRF-TOKEN", csrf!.Token);
        return client;
    }

    public static async Task<HttpResponseMessage> LoginAsync(HttpClient client, string user, string password, string? returnUrl = null)
    {
        var token = await GetFormTokenAsync(client, "/admin/login");
        return await client.PostAsync("/admin/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Username"] = user, ["Password"] = password, ["ReturnUrl"] = returnUrl ?? "",
            ["__RequestVerificationToken"] = token
        }));
    }

    public static async Task<string> GetFormTokenAsync(HttpClient client, string url)
    {
        var html = await client.GetStringAsync(url);
        var m = TokenInput().Match(html);
        if (!m.Success) throw new InvalidOperationException("No anti-forgery token on " + url);
        return System.Net.WebUtility.HtmlDecode(m.Groups[1].Value);
    }

    [GeneratedRegex(@"name=""__RequestVerificationToken""[^>]*value=""([^""]+)""")]
    private static partial Regex TokenInput();

    private sealed record CsrfResponse(string Token);

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing) return;
        SqliteConnection.ClearAllPools();
        if (!_ownsDirectory) return;
        try { Directory.Delete(Path.GetDirectoryName(DbPath)!, recursive: true); } catch (IOException) { }
    }
}
