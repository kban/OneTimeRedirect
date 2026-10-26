using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Data.Sqlite;

namespace OneTimeRedirect.Web.Pages.Admin;

public sealed class CreateModel(LinkPublisher publisher, SubmissionGuard guard, ILogger<CreateModel> log) : PageModel
{
    [BindProperty] public string? TargetUrl { get; set; }
    [BindProperty] public DateTime? ExpiresAtUtc { get; set; }
    [BindProperty] public string? Description { get; set; }
    /// <summary>Random id rendered into the form; makes a repeated submission of the same form harmless.</summary>
    [BindProperty] public string? SubmissionId { get; set; }

    public void OnGet() => SubmissionId = SubmissionGuard.NewId();

    public async Task<IActionResult> OnPostAsync(CancellationToken ct)
    {
        if (!SubmissionGuard.IsValid(SubmissionId)) SubmissionId = SubmissionGuard.NewId(); // keeps old clients working, no dedupe for them

        DateTimeOffset? expires = ExpiresAtUtc is { } dt ? new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc)) : null;
        var error = LinkPublisher.Validate(TargetUrl, expires, Description, out var target);
        if (error is not null)
        {
            ModelState.AddModelError(string.Empty, error);
            return Page();                                  // stay on the form, entered values are kept
        }

        if (!guard.TryReserve(SubmissionId!))
        {
            TempData["Message"] = "Эта форма уже была отправлена. Если ссылки нет в списке, создайте её заново.";
            return SeeOther("/admin");
        }

        var created = false;
        try
        {
            // The raw token exists only inside this call (encrypted into the database); it is never put in
            // a URL, TempData or cookie.
            await publisher.CreateAsync(target!, expires, Description, Request, ct);
            created = true;
        }
        catch (SqliteException ex)
        {
            log.LogError("Link creation failed: SQLite error {Code}/{ExtendedCode}", ex.SqliteErrorCode, ex.SqliteExtendedErrorCode);
            ModelState.AddModelError(string.Empty, "Не удалось сохранить ссылку из-за ошибки базы данных. Ничего не создано: повторите отправку формы.");
            return new PageResult { StatusCode = StatusCodes.Status503ServiceUnavailable };
        }
        finally
        {
            if (!created) guard.Release(SubmissionId!);     // failed attempts must stay retryable
        }

        TempData["Message"] = "Ссылка успешно создана";
        return SeeOther("/admin");                          // Post/Redirect/Get: the new link is the first row of the list
    }

    private IActionResult SeeOther(string url)
    {
        Response.Headers.Location = url;
        return new StatusCodeResult(StatusCodes.Status303SeeOther);
    }
}
