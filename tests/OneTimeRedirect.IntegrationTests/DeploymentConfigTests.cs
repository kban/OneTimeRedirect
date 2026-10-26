using System.Text.RegularExpressions;

namespace OneTimeRedirect.IntegrationTests;

/// <summary>Static checks of deploy/: Caddy log redaction and HSTS, systemd file permissions, publish layout.</summary>
public sealed partial class DeploymentConfigTests
{
    private static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "OneTimeRedirect.slnx"))) return dir.FullName;
        throw new InvalidOperationException("Repository root not found.");
    }

    private static string Read(string relative) => File.ReadAllText(Path.Combine(Root, relative));

    [GeneratedRegex(@"request>uri\s+regexp\s+(\S+)\s+(\S+)")]
    private static partial Regex UriFilter();

    [Theory]
    [InlineData("/r/AbCdEfGhIjKlMnOpQrStUvWxYz012345")]
    [InlineData("/r/AbCdEfGhIjKlMnOpQrStUvWxYz012345?utm=1")]
    [InlineData("/R/AbCdEfGhIjKlMnOpQrStUvWxYz012345")]
    [InlineData("/r/AbCdEfGhIjKlMnOpQrStUvWxYz012345/")]
    public void Caddy_RedactsTokensInLoggedUris(string uri)
    {
        var m = UriFilter().Match(Read("deploy/Caddyfile"));
        Assert.True(m.Success, "Caddyfile must filter request>uri");
        var redacted = Regex.Replace(uri, m.Groups[1].Value, m.Groups[2].Value);
        Assert.DoesNotContain("AbCdEfGhIjKlMnOpQrStUvWxYz012345", redacted);
    }

    [Fact]
    public void Caddy_KeepsOtherUris_AndSendsHsts()
    {
        var caddy = Read("deploy/Caddyfile");
        var m = UriFilter().Match(caddy);
        Assert.Equal("/admin/links/1/edit", Regex.Replace("/admin/links/1/edit", m.Groups[1].Value, m.Groups[2].Value));
        Assert.Matches(@"header\s+Strict-Transport-Security\s+""max-age=\d{7,}", caddy);
    }

    [Fact]
    public void Systemd_KeepsStateOwnerOnly_AndRunsThePublishedApphost()
    {
        var unit = Read("deploy/one-time-redirect.service");
        Assert.Contains("UMask=0077", unit);
        Assert.Contains("StateDirectory=one-time-redirect", unit);
        Assert.Contains("StateDirectoryMode=0700", unit);
        Assert.Contains("Environment=ASPNETCORE_ENVIRONMENT=Production", unit);
        Assert.Contains("Environment=REDIRECT_DB_PATH=/var/lib/one-time-redirect/", unit);
        // The apphost of a linux-x64 publish is named after the assembly.
        Assert.Contains("ExecStart=/opt/one-time-redirect/" + typeof(Program).Assembly.GetName().Name + "\n", unit.Replace("\r", ""));
    }

    [Fact]
    public void PublishOutput_IsIgnoredByGit()
    {
        var ignore = Read(".gitignore").Replace("\r", "").Split('\n');
        Assert.Contains("deploy/publish/", ignore);
    }
}
