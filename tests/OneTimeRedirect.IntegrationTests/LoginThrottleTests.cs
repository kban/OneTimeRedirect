using System.Net;
using Microsoft.Extensions.DependencyInjection;
using OneTimeRedirect.Web;

namespace OneTimeRedirect.IntegrationTests;

public sealed class LoginThrottleTests
{
    private static async Task<HttpResponseMessage> PostLoginAsync(HttpClient client, string token, string password) =>
        await client.PostAsync("/admin/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Username"] = AppFactory.AdminUser, ["Password"] = password, ["__RequestVerificationToken"] = token
        }));

    [Fact]
    public async Task FortyParallelWrongPasswords_CheckNoMoreThanTheLimit()
    {
        using var app = new AppFactory(maxLoginFailures: 3);
        using var client = app.CreateAnonymousClient();
        var token = await AppFactory.GetFormTokenAsync(client, "/admin/login");

        var start = new TaskCompletionSource();
        var tasks = Enumerable.Range(0, 40).Select(async i =>
        {
            await start.Task;
            using var resp = await PostLoginAsync(client, token, "wrong-password-" + i);
            return resp.StatusCode;
        }).ToArray();
        start.SetResult();
        var codes = await Task.WhenAll(tasks);

        Assert.Equal(3, codes.Count(c => c == HttpStatusCode.Unauthorized));
        Assert.Equal(37, codes.Count(c => c == HttpStatusCode.TooManyRequests));

        // The correct password does not get through the lockout either.
        using var correct = await PostLoginAsync(client, token, AppFactory.AdminPassword);
        Assert.Equal(HttpStatusCode.TooManyRequests, correct.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/admin")).StatusCode);
    }

    [Fact]
    public async Task AllCheckSlotsBusy_Returns503_DoesNotAuthenticate_AndDoesNotCountAsFailure()
    {
        using var app = new AppFactory(maxLoginFailures: 1, maxPasswordChecks: 1);
        using var client = app.CreateAnonymousClient();
        var token = await AppFactory.GetFormTokenAsync(client, "/admin/login");
        var limiter = app.Services.GetRequiredService<PasswordCheckLimiter>();

        using (var held = await limiter.TryEnterAsync(CancellationToken.None))
        {
            Assert.NotNull(held);
            using var busy = await PostLoginAsync(client, token, AppFactory.AdminPassword); // waits for the 5 s timeout
            Assert.Equal(HttpStatusCode.ServiceUnavailable, busy.StatusCode);
            Assert.False(busy.Headers.Contains("Set-Cookie") &&
                         busy.Headers.GetValues("Set-Cookie").Any(c => c.StartsWith("__Host-otr-admin=")));
        }

        // With a limit of one failure, the busy attempt must have been given back.
        using var ok = await PostLoginAsync(client, token, AppFactory.AdminPassword);
        Assert.Equal(HttpStatusCode.Redirect, ok.StatusCode);
    }

    [Fact]
    public void UncheckedAttempt_IsGivenBack()
    {
        var throttle = new LoginThrottle(1, TimeSpan.FromMinutes(15));
        var attempt = throttle.TryBegin("c");
        Assert.NotNull(attempt);
        Assert.Null(throttle.TryBegin("c")); // the in-flight reservation already uses the only slot
        attempt.Dispose();
        Assert.NotNull(throttle.TryBegin("c"));
    }

    [Fact]
    public void CheckedAttempt_WithoutSuccess_CountsAsFailure()
    {
        var throttle = new LoginThrottle(2, TimeSpan.FromMinutes(15));
        for (var i = 0; i < 2; i++)
        {
            using var attempt = throttle.TryBegin("c")!;
            attempt.MarkChecked(); // e.g. an exception after the password check: fails closed
        }
        Assert.Null(throttle.TryBegin("c"));
        Assert.NotNull(throttle.TryBegin("other"));
    }

    [Fact]
    public void Success_ClearsFailures()
    {
        var throttle = new LoginThrottle(2, TimeSpan.FromMinutes(15));
        using (var failed = throttle.TryBegin("c")!) failed.MarkChecked();
        using (var ok = throttle.TryBegin("c")!) { ok.MarkChecked(); ok.Succeeded(); }
        using var a = throttle.TryBegin("c");
        using var b = throttle.TryBegin("c");
        Assert.NotNull(a);
        Assert.NotNull(b);
    }

    [Fact]
    public void ExpiredWindow_StartsOver()
    {
        var throttle = new LoginThrottle(1, TimeSpan.FromMilliseconds(50));
        using (var a = throttle.TryBegin("c")!) a.MarkChecked();
        Assert.Null(throttle.TryBegin("c"));
        Thread.Sleep(100);
        Assert.NotNull(throttle.TryBegin("c"));
    }

    [Fact]
    public async Task CheckLimiter_TimesOut_InsteadOfWaitingForever()
    {
        var limiter = new PasswordCheckLimiter(1, TimeSpan.FromMilliseconds(100));
        using var held = await limiter.TryEnterAsync(CancellationToken.None);
        Assert.NotNull(held);
        Assert.Null(await limiter.TryEnterAsync(CancellationToken.None));
    }

    [Fact]
    public async Task CheckLimiter_CancelledWait_DoesNotLeakSlots_AndDoubleDisposeReleasesOnce()
    {
        var limiter = new PasswordCheckLimiter(1, TimeSpan.FromMilliseconds(100));
        var held = await limiter.TryEnterAsync(CancellationToken.None);
        Assert.NotNull(held);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => limiter.TryEnterAsync(cts.Token));

        held.Dispose();
        held.Dispose();
        using var again = await limiter.TryEnterAsync(CancellationToken.None);
        Assert.NotNull(again);
        Assert.Null(await limiter.TryEnterAsync(CancellationToken.None)); // still only one slot
    }

    [Theory]
    [InlineData("203.0.113.7", "203.0.113.7")]
    [InlineData("::ffff:203.0.113.7", "203.0.113.7")]
    [InlineData("2001:db8:1:2:aaaa:bbbb:cccc:dddd", "2001:db8:1:2::/64")]
    [InlineData("2001:db8:1:2::1", "2001:db8:1:2::/64")]
    public void ClientKey_GroupsIpv6By64(string address, string expected) =>
        Assert.Equal(expected, ClientKey.From(IPAddress.Parse(address)));

    [Fact]
    public void ClientKey_WithoutAddress_IsShared() => Assert.Equal("unknown", ClientKey.From(null));
}
