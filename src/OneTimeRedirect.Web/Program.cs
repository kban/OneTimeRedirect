using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Data.Sqlite;
using OneTimeRedirect.Web;
using OneTimeRedirect.Web.Operations;

// Helper for operators: `OneTimeRedirect.Web hash-password` prints a value for REDIRECT_ADMIN_PASSWORD_HASH.
if (args is ["hash-password"]) return PasswordPrompt.Run();
// Backup / restore commands (see deploy/backup.sh, deploy/restore.sh): they never start the web host.
if (OpsCli.IsOpsCommand(args)) return OpsCli.Run(args);

var builder = WebApplication.CreateBuilder(args);
// Request-path loggers would write one-time tokens (/r/{token}); keep them off whatever the config says.
// Hosting.Diagnostics only logs request start/finish (with the path) and opens the per-request logging scope
// that carries RequestPath, so it is switched off entirely. Unhandled request exceptions are still logged
// by Kestrel (Microsoft.AspNetCore.Server.Kestrel) and startup failures by Microsoft.Extensions.Hosting.
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.None);
builder.Logging.AddFilter("Microsoft.AspNetCore.Routing", LogLevel.Warning);
var dbPath = builder.Configuration["REDIRECT_DB_PATH"] ?? "data/redirects.db";
// A restore that has not finished leaves RESTORE_LOCK next to the database: never serve links from that state.
RestoreLock.EnsureNotLocked(dbPath);
var adminHash = builder.Configuration["REDIRECT_ADMIN_PASSWORD_HASH"];
if (!PasswordHasher.IsWellFormed(adminHash))
    throw new InvalidOperationException(
        "Set REDIRECT_ADMIN_PASSWORD_HASH (generate it with: OneTimeRedirect.Web hash-password).");
var adminUser = builder.Configuration["REDIRECT_ADMIN_USER"] is { Length: > 0 } u ? u : "admin";
var maxLoginFailures = PositiveInt("ADMIN_LOGIN_MAX_FAILURES", 5);
var maxPasswordChecks = PositiveInt("ADMIN_LOGIN_MAX_CONCURRENT_CHECKS", 2);
var publicRateLimit = PositiveInt("PUBLIC_RATE_LIMIT_PER_MINUTE", 60);

string? publicBaseUrl = null;
if (builder.Configuration["PUBLIC_BASE_URL"] is { Length: > 0 } configuredBase &&
    !UrlValidator.TryValidateBaseUrl(configuredBase, out publicBaseUrl))
    throw new InvalidOperationException("PUBLIC_BASE_URL must be an HTTPS origin such as https://example.com (no path or query).");
if (!builder.Environment.IsDevelopment())
{
    if (publicBaseUrl is null)
        throw new InvalidOperationException("Set PUBLIC_BASE_URL (e.g. https://example.com): links must not be built from the Host header.");
    // Caddy passes the original Host through; unless hosts are listed explicitly, accept only the public one.
    if (builder.Configuration["AllowedHosts"] is null or "" or "*")
        builder.Configuration["AllowedHosts"] = new Uri(publicBaseUrl).IdnHost;
}

builder.Services.AddSingleton(new LinkStore(dbPath));
builder.Services.AddSingleton(new PublicBaseUrl(publicBaseUrl));
builder.Services.AddSingleton<LinkTokenProtector>();
builder.Services.AddSingleton<SubmissionGuard>();
builder.Services.AddSingleton<LinkPublisher>();
builder.Services.AddSingleton(new AdminCredentials(adminUser, adminHash!));
builder.Services.AddSingleton(new LoginThrottle(maxLoginFailures, TimeSpan.FromMinutes(15)));
builder.Services.AddSingleton(new PasswordCheckLimiter(maxPasswordChecks, TimeSpan.FromSeconds(5)));

// Per-client cap on /r/: a rejected request (429) never reaches ConsumeAsync, so it cannot burn a link.
builder.Services.AddRateLimiter(o =>
{
    o.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    o.AddPolicy("public", ctx => RateLimitPartition.GetFixedWindowLimiter(
        ClientKey.From(ctx.Connection.RemoteIpAddress),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = publicRateLimit, Window = TimeSpan.FromMinutes(1) }));
});

// Persist keys next to the database so cookies and anti-forgery tokens survive restarts.
builder.Services.AddDataProtection()
    .SetApplicationName("OneTimeRedirect")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbPath))!, "keys")));

builder.Services.Configure<ForwardedHeadersOptions>(o =>
    o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto); // loopback proxy (Caddy) only

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.Cookie.Name = "__Host-otr-admin";
        o.Cookie.HttpOnly = true;
        o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
        o.Cookie.SameSite = SameSiteMode.Strict;
        o.LoginPath = "/admin/login";
        o.AccessDeniedPath = "/admin/login";
        o.ExpireTimeSpan = TimeSpan.FromHours(8);
        o.SlidingExpiration = false;
        o.Events.OnRedirectToLogin = ctx =>
        {
            if (ctx.Request.Path.StartsWithSegments("/admin/api")) ctx.Response.StatusCode = StatusCodes.Status401Unauthorized;
            else ctx.Response.Redirect(ctx.RedirectUri);
            return Task.CompletedTask;
        };
    });
builder.Services.AddAuthorization();
builder.Services.AddAntiforgery(o =>
{
    o.HeaderName = "X-CSRF-TOKEN";
    o.Cookie.Name = "__Host-otr-csrf";
    o.Cookie.HttpOnly = true;
    o.Cookie.SecurePolicy = CookieSecurePolicy.Always;
    o.Cookie.SameSite = SameSiteMode.Strict;
});
builder.Services.AddRazorPages(o =>
{
    o.Conventions.AuthorizeFolder("/Admin");
    o.Conventions.AllowAnonymousToPage("/Admin/Login");
});

var app = builder.Build();
await app.Services.GetRequiredService<LinkStore>().InitializeAsync();

app.UseForwardedHeaders();
app.Use(async (ctx, next) =>
{
    ctx.Response.Headers.CacheControl = "no-store";
    ctx.Response.Headers["Referrer-Policy"] = "no-referrer";
    ctx.Response.Headers["X-Content-Type-Options"] = "nosniff";
    ctx.Response.Headers["X-Frame-Options"] = "DENY";
    ctx.Response.Headers["X-Robots-Tag"] = "noindex, nofollow";
    ctx.Response.Headers["Content-Security-Policy"] = "default-src 'self'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
    await next();
});
app.UseRateLimiter();
app.UseStaticFiles();
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));

// Do not log token paths. Avoid ASP.NET HTTP logging of sensitive URL paths.
app.MapGet("/r/{token}", async (string token, HttpRequest request, LinkStore store, ILogger<Program> log, CancellationToken ct) =>
{
    if (!TokenService.IsValid(token)) return Results.NotFound();
    // Known previews/prefetches get the same answer as an unknown token: nothing consumed, no TargetUrl.
    if (PreviewDetector.Classify(request.Headers) is { } preview)
    {
        log.LogInformation("Preview request not served: {Kind}", preview); // never log the path/token
        return Results.NotFound();
    }
    try
    {
        var url = await store.ConsumeAsync(TokenService.Hash(token), ct);
        return url is null ? Results.NotFound() : Results.Redirect(url, permanent: false);
    }
    catch (SqliteException ex)
    {
        // Any storage failure (BUSY, LOCKED, FULL, IOERR, READONLY, CANTOPEN, ...) means no confirmed UsedAt,
        // so no 302. Only SQLite codes are logged: the message never contains bound values, the path is not used.
        if (ex.SqliteErrorCode is SQLitePCL.raw.SQLITE_BUSY or SQLitePCL.raw.SQLITE_LOCKED)
            log.LogWarning("Redirect not served: database busy (SQLite {Code}/{ExtendedCode})",
                ex.SqliteErrorCode, ex.SqliteExtendedErrorCode);
        else
            log.LogError(ex, "Redirect not served: SQLite error {Code}/{ExtendedCode}",
                ex.SqliteErrorCode, ex.SqliteExtendedErrorCode);
        request.HttpContext.Response.Headers.RetryAfter = "5";
        return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
    }
}).RequireRateLimiting("public");

// HEAD must not consume a token; no Location is returned.
app.MapMethods("/r/{token}", ["HEAD"], (string token) =>
    Results.NotFound()).RequireRateLimiting("public");

// Admin API: same cookie session as the Razor Pages. Mutating calls need the anti-forgery
// token (GET /admin/api/csrf) in the X-CSRF-TOKEN header.
var admin = app.MapGroup("/admin/api").RequireAuthorization();
admin.AddEndpointFilter(async (context, next) =>
{
    var http = context.HttpContext;
    if (!HttpMethods.IsGet(http.Request.Method))
    {
        try { await http.RequestServices.GetRequiredService<IAntiforgery>().ValidateRequestAsync(http); }
        catch (AntiforgeryValidationException) { return Results.BadRequest(new { error = "Invalid anti-forgery token." }); }
    }
    return await next(context);
});

admin.MapGet("/csrf", (HttpContext ctx, IAntiforgery af) =>
    Results.Ok(new { token = af.GetAndStoreTokens(ctx).RequestToken }));

admin.MapPost("/links", async (CreateLinkRequest req, LinkPublisher publisher, HttpContext ctx, CancellationToken ct) =>
{
    if (!UrlValidator.TryValidate(req.TargetUrl, out var target))
        return Results.BadRequest(new { error = "Only absolute HTTP or HTTPS URLs without embedded credentials are allowed." });
    if (req.ExpiresAt is not null && req.ExpiresAt <= DateTimeOffset.UtcNow)
        return Results.BadRequest(new { error = "ExpiresAt must be in the future." });
    if (req.Description?.Length > 500) return Results.BadRequest(new { error = "Description is too long." });
    var (id, link) = await publisher.CreateAsync(target!, req.ExpiresAt, req.Description, ctx.Request, ct);
    return Results.Created($"/admin/api/links/{id}", new { id, link });
});

admin.MapGet("/links", async (LinkStore store, CancellationToken ct) =>
    Results.Ok(await store.ListAsync(ct)));

admin.MapPost("/links/{id:long}/revoke", async (long id, LinkStore store, CancellationToken ct) =>
    await store.RevokeAsync(id, ct) ? Results.NoContent() : Results.NotFound());

admin.MapPut("/links/{id:long}", async (long id, UpdateLinkRequest req, LinkStore store, CancellationToken ct) =>
{
    if (!UrlValidator.TryValidate(req.TargetUrl, out var target))
        return Results.BadRequest(new { error = "Invalid target URL (only HTTP or HTTPS)." });
    if (req.ExpiresAt is not null && req.ExpiresAt <= DateTimeOffset.UtcNow)
        return Results.BadRequest(new { error = "ExpiresAt must be in the future." });
    if (req.Description?.Length > 500) return Results.BadRequest(new { error = "Description is too long." });
    return await store.UpdateAsync(id, target!, req.ExpiresAt, req.Description, ct)
        ? Results.NoContent() : Results.NotFound();
});

app.Run();
return 0;

int PositiveInt(string key, int fallback) =>
    int.TryParse(builder.Configuration[key], out var value) && value > 0 ? value : fallback;

public partial class Program;

public sealed record CreateLinkRequest(string TargetUrl, DateTimeOffset? ExpiresAt, string? Description);
public sealed record UpdateLinkRequest(string TargetUrl, DateTimeOffset? ExpiresAt, string? Description);

public static class PasswordPrompt
{
    public static int Run()
    {
        Console.Error.Write("Admin password (min 12 chars): ");
        var password = Console.IsInputRedirected ? Console.ReadLine() : ReadHidden();
        if (password is null || password.Length < 12)
        {
            Console.Error.WriteLine("Password must be at least 12 characters.");
            return 1;
        }
        Console.WriteLine(PasswordHasher.Hash(password));
        return 0;
    }

    private static string ReadHidden()
    {
        var sb = new System.Text.StringBuilder();
        while (Console.ReadKey(intercept: true) is var k && k.Key != ConsoleKey.Enter)
        {
            if (k.Key == ConsoleKey.Backspace) { if (sb.Length > 0) sb.Length--; }
            else sb.Append(k.KeyChar);
        }
        Console.Error.WriteLine();
        return sb.ToString();
    }
}
