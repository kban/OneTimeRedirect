using OneTimeRedirect.Web;

namespace OneTimeRedirect.IntegrationTests;

public sealed class UrlValidatorTests
{
    [Theory]
    [InlineData("https://ya.ru", "https://ya.ru")]
    [InlineData("http://ya.ru", "http://ya.ru")]
    [InlineData("www.ya.ru", "https://www.ya.ru")]
    [InlineData("ya.ru/path?q=1", "https://ya.ru/path?q=1")]
    [InlineData("http://example.com/page#section", "http://example.com/page#section")]
    [InlineData("https://example.com/a/b?x=1&y=2#frag", "https://example.com/a/b?x=1&y=2#frag")]
    [InlineData("example.com/a#frag", "https://example.com/a#frag")]
    [InlineData("HTTP://EXAMPLE.com/Path", "http://example.com/Path")]       // scheme/host are case-insensitive, the path is not
    [InlineData("ya.ru:8080/x", "https://ya.ru:8080/x")]                      // host:port is not mistaken for a scheme
    [InlineData("localhost:3000", "https://localhost:3000")]
    [InlineData("http://127.0.0.1:8080/health", "http://127.0.0.1:8080/health")]
    [InlineData("http://[::1]:8080/x", "http://[::1]:8080/x")]
    [InlineData("  https://ya.ru/a  ", "https://ya.ru/a")]
    [InlineData("https://ya.ru/", "https://ya.ru/")]                           // a typed trailing slash stays
    [InlineData("http://example.com/a%20b?q=%0D%0A", "http://example.com/a%20b?q=%0D%0A")] // encoded CRLF is harmless and kept encoded
    public void Accepts_AndNormalizes(string input, string expected)
    {
        Assert.True(UrlValidator.TryValidate(input, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Fact]
    public void ExplicitHttp_IsNeverUpgradedToHttps()
    {
        Assert.True(UrlValidator.TryValidate("http://example.com/x", out var n));
        Assert.StartsWith("http://", n);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JAVASCRIPT:alert(1)")]
    [InlineData("data:text/html;base64,PHNjcmlwdD4=")]
    [InlineData("file:///etc/passwd")]
    [InlineData("ftp://example.com")]
    [InlineData("ws://example.com")]
    [InlineData("chrome://settings")]
    [InlineData("mailto:a@example.com")]
    [InlineData("vbscript:msgbox(1)")]
    [InlineData("//example.com")]
    [InlineData("http:/example.com")]
    [InlineData("http:example.com")]
    [InlineData("https://")]
    [InlineData("http://")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Rejects_UnsupportedSchemesAndMalformedInput(string? input) =>
        Assert.False(UrlValidator.TryValidate(input, out _));

    [Theory]
    [InlineData("https://user:pass@example.com")]
    [InlineData("http://user@example.com")]
    [InlineData("user@example.com")]
    [InlineData("user:pass@example.com")]
    public void Rejects_EmbeddedCredentials(string input) => Assert.False(UrlValidator.TryValidate(input, out _));

    [Theory]
    [InlineData("http://example.com/\r\nSet-Cookie: a=b")]
    [InlineData("http://example.com/\n")]
    [InlineData("http://example.com/\r")]
    [InlineData("ya.ru\t")]
    [InlineData("\nhttp://example.com")]
    [InlineData("http://exa\0mple.com")]
    [InlineData("http://example.com/\u0001")]
    [InlineData("http://example.com/\u007F")]
    [InlineData("http://example.com\\evil.com")]
    [InlineData("http:\\\\example.com")]
    [InlineData("http://‮example.com")]
    [InlineData("http://exa​mple.com")]
    [InlineData("http://exa mple.com")]
    [InlineData("https://[::1")]
    [InlineData("https://example.com:99999")]
    public void Rejects_ControlCharactersCrlfAndInvalidUrls(string input) =>
        Assert.False(UrlValidator.TryValidate(input, out _));

    [Fact]
    public void Rejects_TooLong()
    {
        Assert.True(UrlValidator.TryValidate("https://example.com/" + new string('a', 2000), out _));
        Assert.False(UrlValidator.TryValidate("https://example.com/" + new string('a', 2100), out _));
    }

    [Theory]
    [InlineData("https://example.com", "https://example.com")]
    [InlineData("HTTPS://Example.COM/", "https://example.com")]
    public void PublicBaseUrl_StaysHttpsOnly_Accepted(string input, string expected)
    {
        Assert.True(UrlValidator.TryValidateBaseUrl(input, out var origin));
        Assert.Equal(expected, origin);
    }

    [Theory]
    [InlineData("http://example.com")]      // public links must stay HTTPS even though targets may be HTTP
    [InlineData("example.com")]             // no implicit scheme for the public base
    [InlineData("https://example.com/path")]
    [InlineData("https://example.com?x=1")]
    [InlineData("https://example.com#x")]
    public void PublicBaseUrl_StaysHttpsOnly_Rejected(string input) =>
        Assert.False(UrlValidator.TryValidateBaseUrl(input, out _));
}
