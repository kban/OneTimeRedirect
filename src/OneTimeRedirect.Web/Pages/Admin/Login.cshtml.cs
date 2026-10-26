using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace OneTimeRedirect.Web.Pages.Admin;

public sealed class LoginModel(AdminCredentials credentials, LoginThrottle throttle, PasswordCheckLimiter checks) : PageModel
{
    [BindProperty(SupportsGet = true)] public string? ReturnUrl { get; set; }
    [BindProperty] public string? Username { get; set; }
    [BindProperty] public string? Password { get; set; }
    public string? Error { get; private set; }

    public IActionResult OnGet() =>
        User.Identity?.IsAuthenticated == true ? LocalRedirect(SafeReturnUrl()) : Page();

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        // Disposing gives back an attempt that never reached the password check (busy, cancelled).
        using var attempt = throttle.TryBegin(ClientKey.From(HttpContext.Connection.RemoteIpAddress));
        if (attempt is null)
        {
            Error = "Too many failed attempts. Try again later.";
            return new PageResult { StatusCode = StatusCodes.Status429TooManyRequests };
        }

        bool valid;
        using (var slot = await checks.TryEnterAsync(ct))
        {
            if (slot is null)
            {
                Error = "Server is busy. Try again later.";
                Response.Headers.RetryAfter = "5";
                return new PageResult { StatusCode = StatusCodes.Status503ServiceUnavailable };
            }
            attempt.MarkChecked(); // from here the attempt counts as a failure unless it succeeds
            // Always run the password hash so timing does not reveal whether the user name matched.
            var passwordOk = PasswordHasher.Verify(Password ?? "", credentials.PasswordHash);
            var userOk = CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(Encoding.UTF8.GetBytes(Username ?? "")),
                SHA256.HashData(Encoding.UTF8.GetBytes(credentials.UserName)));
            valid = passwordOk && userOk;
        }
        if (!valid)
        {
            Error = "Invalid username or password.";
            return new PageResult { StatusCode = StatusCodes.Status401Unauthorized };
        }

        attempt.Succeeded();
        var identity = new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, credentials.UserName), new Claim(ClaimTypes.Role, "Admin")],
            CookieAuthenticationDefaults.AuthenticationScheme);
        await HttpContext.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity),
            new AuthenticationProperties { IsPersistent = false, ExpiresUtc = DateTimeOffset.UtcNow.AddHours(8) });
        return LocalRedirect(SafeReturnUrl());
    }

    // Only site-local paths are accepted, so ReturnUrl cannot become an open redirect.
    private string SafeReturnUrl() =>
        !string.IsNullOrEmpty(ReturnUrl) && Url.IsLocalUrl(ReturnUrl) && !ReturnUrl.StartsWith("/admin/api", StringComparison.OrdinalIgnoreCase)
            ? ReturnUrl : "/admin";
}
