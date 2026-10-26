namespace OneTimeRedirect.IntegrationTests;

/// <summary>
/// Static guards for the backup/restore deployment files. The behaviour itself is exercised end to end by
/// tests/e2e (Debian 13 container); these checks keep the safety-critical ordering from being edited away.
/// </summary>
public sealed class BackupDeploymentTests
{
    private static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "OneTimeRedirect.slnx"))) return dir.FullName;
        throw new InvalidOperationException("Repository root not found.");
    }

    private static string Read(string relative) => File.ReadAllText(Path.Combine(Root, relative));

    [Theory]
    [InlineData("deploy/backup.sh")]
    [InlineData("deploy/restore.sh")]
    [InlineData("tests/e2e/inside.sh")]
    public void Scripts_UseLfAndStrictMode(string path)
    {
        var text = Read(path);
        Assert.StartsWith("#!/usr/bin/env bash\n", text);
        Assert.DoesNotContain('\r', text); // CRLF would break the shebang on Linux
        Assert.Contains("set -", text);
    }

    [Fact]
    public void Restore_ReopensPublicAccessOnlyAsItsVeryLastStep_AndNeverFromTheErrorPath()
    {
        var script = Read("deploy/restore.sh");
        const string openGate = "rm -f -- \"$GATE\"";
        Assert.Equal(1, script.Split(openGate).Length - 1);

        var fail = script[script.IndexOf("fail() {", StringComparison.Ordinal)..script.IndexOf("trap 'fail", StringComparison.Ordinal)];
        Assert.DoesNotContain(openGate, fail);
        Assert.Contains(": >\"$GATE\"", fail); // the error path closes (or keeps closed)

        var gate = script.IndexOf(openGate, StringComparison.Ordinal);
        foreach (var earlier in new[] { "\"$BIN\" restore-db", "verify_live stopped", "\"$SYSTEMCTL\" start", "service healthy", "verify_live ||" })
            Assert.True(script.IndexOf(earlier, StringComparison.Ordinal) is var i and >= 0 && i < gate, $"'{earlier}' must come before the gate is opened");

        // The service must be stopped and locked before the database is touched; the lock goes away only after verification.
        Assert.True(script.IndexOf("# 3. Block automatic starts", StringComparison.Ordinal) < script.IndexOf("\"$BIN\" restore-db", StringComparison.Ordinal));
        Assert.True(script.IndexOf("verify_live stopped", StringComparison.Ordinal) < script.IndexOf("rm -f -- \"$LOCK_MARKER\"", StringComparison.Ordinal));
        Assert.True(script.IndexOf("rm -f -- \"$LOCK_MARKER\"", StringComparison.Ordinal) < script.IndexOf("\"$SYSTEMCTL\" start", StringComparison.Ordinal));
        Assert.Contains("flock -n 9", script);
    }

    [Fact]
    public void Backup_NeverCountsALocalSnapshotAsSuccess_AndOnlyPrunesConfirmedOnes()
    {
        var script = Read("deploy/backup.sh");
        Assert.Contains("RCLONE_REMOTE is not set", script);
        Assert.Contains("--download", script);                      // byte-for-byte confirmation
        Assert.Contains("NOT yet off-site", script);
        Assert.Contains("[[ $i -le $KEEP_MIN || ! -e \"$f.uploaded\" ]] && continue", script);
        Assert.DoesNotContain("cp ", script.Replace("rclone copyto", ""));   // snapshots come from the SQLite backup API, not cp
        Assert.DoesNotContain("sqlite3", script);
    }

    [Fact]
    public void Caddy_HasMaintenanceGate_Answering503BeforeProxying()
    {
        var caddy = Read("deploy/Caddyfile");
        Assert.Matches(@"@maintenance\s+file\s*\{\s*root /etc\s+try_files /one-time-redirect\.maintenance\s*\}", caddy);
        var gate = caddy.IndexOf("handle @maintenance", StringComparison.Ordinal);
        Assert.True(gate > 0 && gate < caddy.IndexOf("reverse_proxy 127.0.0.1", StringComparison.Ordinal));
        Assert.Contains("respond \"Service temporarily unavailable\" 503", caddy);
    }

    [Fact]
    public void Systemd_UnitsBlockStartsDuringRestore_AndUploadRunsAsRootOnlyAfterSnapshot()
    {
        var app = Read("deploy/one-time-redirect.service");
        Assert.Contains("ConditionPathExists=!/var/lib/one-time-redirect/RESTORE_LOCK", app);

        var backup = Read("deploy/one-time-redirect-backup.service");
        Assert.Contains("ConditionPathExists=!/var/lib/one-time-redirect/RESTORE_LOCK", backup);
        Assert.Contains("User=redirect", backup);
        Assert.Contains("ExecStart=/usr/local/sbin/otr-backup snapshot", backup);
        Assert.Contains("ExecStartPost=+/usr/local/sbin/otr-backup upload", backup);
        Assert.Contains("UMask=0077", backup);

        var timer = Read("deploy/one-time-redirect-backup.timer");
        Assert.Contains("Persistent=true", timer);
        Assert.Contains("Unit=one-time-redirect-backup.service", timer);
    }
}
