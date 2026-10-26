using System.Net;
using System.Net.Http.Json;
using Microsoft.Data.Sqlite;
using OneTimeRedirect.Web.Operations;

namespace OneTimeRedirect.IntegrationTests;

public sealed class LinuxFactAttribute : FactAttribute
{
    public LinuxFactAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "Unix file modes are not available on Windows (covered by the Linux e2e run).";
    }
}

/// <summary>Scratch directory holding a database, its backups and the diagnostic copies.</summary>
public sealed class Workspace : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "otr-dr-" + Guid.NewGuid().ToString("N"));
    public string Db => Path.Combine(Root, "data", "redirects.db");
    public string Backups => Path.Combine(Root, "backups");
    public string Saved => Path.Combine(Root, "saved");
    public Workspace() => Directory.CreateDirectory(Path.GetDirectoryName(Db)!);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(Root, recursive: true); } catch (IOException) { }
    }
}

public sealed class BackupRestoreTests
{
    private sealed record Created(long Id, string Link);

    private const string Browser = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/126.0.0.0 Safari/537.36";

    private static async Task<(long Id, string Path)> CreateAsync(HttpClient client, string name)
    {
        var resp = await client.PostAsJsonAsync("/admin/api/links", new { targetUrl = "https://example.com/" + name });
        var created = (await resp.Content.ReadFromJsonAsync<Created>())!;
        return (created.Id, new Uri(created.Link).AbsolutePath);
    }

    private static async Task<HttpStatusCode> OpenAsync(HttpClient client, string path)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, path);
        req.Headers.TryAddWithoutValidation("User-Agent", Browser);
        using var resp = await client.SendAsync(req);
        return resp.StatusCode;
    }

    private static void Sql(string db, string sql)
    {
        using var conn = DatabaseChecks.Open(db, SqliteOpenMode.ReadWrite);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static long Scalar(string db, string sql) => DatabaseChecks.Scalar(db, sql);

    private static IEnumerable<string> Chain(Exception? ex)
    {
        for (; ex is not null; ex = ex.InnerException) yield return ex.Message;
    }

    private sealed record Scenario(string A, string C, string D, string E, string F, string BackupFile, long EUsedAt, long TotalAtBackup);

    /// <summary>Links at backup time: A,C,D active (C already expired), E used, F revoked. Then A is used and G is created.</summary>
    private static async Task<(Scenario S, string G)> BuildHistoryAsync(Workspace ws)
    {
        using var app = new AppFactory(ws.Db);
        using var client = await app.CreateAdminClientAsync();
        var a = await CreateAsync(client, "a");
        var c = await CreateAsync(client, "c");
        var d = await CreateAsync(client, "d");
        var e = await CreateAsync(client, "e");
        var f = await CreateAsync(client, "f");
        Assert.Equal(HttpStatusCode.Found, await OpenAsync(client, e.Path));
        Assert.Equal(HttpStatusCode.NoContent, (await client.PostAsync($"/admin/api/links/{f.Id}/revoke", null)).StatusCode);
        Sql(ws.Db, $"UPDATE RedirectLinks SET ExpiresAt = 1000 WHERE Id = {c.Id}"); // expired, never revoked

        // The application is still running and its WAL is live while the snapshot is taken.
        var backup = DatabaseBackup.Create(ws.Db, ws.Backups, DateTimeOffset.UtcNow);
        var eUsedAt = Scalar(backup.Path, $"SELECT UsedAt FROM RedirectLinks WHERE Id = {e.Id}");

        Assert.Equal(HttpStatusCode.Found, await OpenAsync(client, a.Path));   // A is used after the backup
        Assert.Equal(HttpStatusCode.NotFound, await OpenAsync(client, a.Path));
        var g = await CreateAsync(client, "g");                                  // created after the backup
        return (new Scenario(a.Path, c.Path, d.Path, e.Path, f.Path, backup.Path, eUsedAt, backup.Links), g.Path);
    }

    [Fact]
    public async Task RestoringAnOldBackup_NeverReopensUsedOrOtherwiseActiveLinks()
    {
        using var ws = new Workspace();
        var (s, g) = await BuildHistoryAsync(ws);

        var result = DatabaseRestore.Run(new RestoreOptions(ws.Db, s.BackupFile, ws.Saved));
        RestoreLock.Release(ws.Db);

        Assert.Equal(s.TotalAtBackup, result.Total);
        Assert.Equal(3, result.Revoked); // A, C (expired) and D
        Assert.Equal(0, Scalar(ws.Db, DatabaseChecks.ActiveSql));

        using var app = new AppFactory(ws.Db);
        using var client = await app.CreateAdminClientAsync();
        foreach (var path in new[] { s.A, s.C, s.D, s.E, s.F, g })
            Assert.Equal(HttpStatusCode.NotFound, await OpenAsync(client, path));

        // History inside the restored state is intact: E keeps its original UsedAt, F stays revoked, nothing was deleted.
        Assert.Equal(1, Scalar(ws.Db, DatabaseChecks.UsedSql));
        Assert.Equal(s.EUsedAt, Scalar(ws.Db, "SELECT UsedAt FROM RedirectLinks WHERE UsedAt IS NOT NULL"));
        Assert.Equal(s.TotalAtBackup, Scalar(ws.Db, DatabaseChecks.TotalSql));
        Assert.Equal(0, Scalar(ws.Db, "SELECT COUNT(*) FROM RedirectLinks WHERE UsedAt IS NULL AND RevokedAt IS NULL AND ExpiresAt IS NOT NULL AND ExpiresAt < 5000"));

        // Normal operation resumes: a new link works exactly once.
        var b = await CreateAsync(client, "b");
        Assert.Equal(HttpStatusCode.Found, await OpenAsync(client, b.Path));
        Assert.Equal(HttpStatusCode.NotFound, await OpenAsync(client, b.Path));
    }

    [Fact]
    public async Task Restore_KeepsDiagnosticCopyOfTheReplacedDatabase()
    {
        using var ws = new Workspace();
        var (s, _) = await BuildHistoryAsync(ws);
        var before = Scalar(ws.Db, DatabaseChecks.ActiveSql);
        Assert.True(before > 0);

        var result = DatabaseRestore.Run(new RestoreOptions(ws.Db, s.BackupFile, ws.Saved));

        var saved = Path.Combine(result.SavedDir, "redirects.db");
        Assert.True(File.Exists(saved));
        Assert.Equal(before, Scalar(saved, DatabaseChecks.ActiveSql)); // the pre-restore state is preserved for diagnostics
        Assert.Contains(Directory.GetFiles(Path.GetDirectoryName(ws.Db)!), f => f.Contains(".replaced-"));
    }

    [Fact]
    public async Task Restore_OntoAFreshServer_WithoutAnExistingDatabase_Works()
    {
        using var src = new Workspace();
        await new OneTimeRedirect.Web.LinkStore(src.Db).InitializeAsync();
        Sql(src.Db, "INSERT INTO RedirectLinks (TokenHash, TargetUrl, CreatedAt) VALUES (x'00', 'https://example.com/', 1)");
        var backup = DatabaseBackup.Create(src.Db, src.Backups, DateTimeOffset.UtcNow);

        using var fresh = new Workspace();
        var result = DatabaseRestore.Run(new RestoreOptions(fresh.Db, backup.Path, fresh.Saved));
        Assert.Equal(1, result.Revoked);
        Assert.Equal(0, Scalar(fresh.Db, DatabaseChecks.ActiveSql));
    }

    [Fact]
    public async Task CorruptBackup_IsRejected_LiveDatabaseUntouched_AndServiceStaysDown()
    {
        using var ws = new Workspace();
        var (s, _) = await BuildHistoryAsync(ws);
        var liveBefore = DatabaseChecks.Sha256Hex(ws.Db);

        var bytes = await File.ReadAllBytesAsync(s.BackupFile);
        for (var i = 100; i < 400; i++) bytes[i] ^= 0xFF;
        var corrupt = Path.Combine(ws.Backups, "redirects-corrupt.db");
        await File.WriteAllBytesAsync(corrupt, bytes);

        // Without a checksum the integrity check must still catch it ...
        var ex = Assert.Throws<RestoreException>(() =>
            DatabaseRestore.Run(new RestoreOptions(ws.Db, corrupt, ws.Saved, AllowMissingChecksum: true)));
        Assert.Contains("not usable", ex.Message);
        Assert.Equal(liveBefore, DatabaseChecks.Sha256Hex(ws.Db));
        AssertServiceRefusesToStart(ws);

        // ... and a modified file is caught by the checksum even if SQLite could still read it.
        var tampered = Path.Combine(ws.Backups, "redirects-tampered.db");
        File.Copy(s.BackupFile, tampered);
        File.Copy(s.BackupFile + ".sha256", tampered + ".sha256");
        var data = await File.ReadAllBytesAsync(tampered);
        data[^1] ^= 0x01;
        await File.WriteAllBytesAsync(tampered, data);
        var ex2 = Assert.Throws<RestoreException>(() => DatabaseRestore.Run(new RestoreOptions(ws.Db, tampered, ws.Saved, Retry: true)));
        Assert.Contains("checksum", ex2.Message);
        Assert.Equal(liveBefore, DatabaseChecks.Sha256Hex(ws.Db));

        // A garbage "database" and a missing checksum are rejected too.
        var junk = Path.Combine(ws.Backups, "junk.db");
        await File.WriteAllTextAsync(junk, "this is not sqlite");
        Assert.Throws<RestoreException>(() => DatabaseRestore.Run(new RestoreOptions(ws.Db, junk, ws.Saved, Retry: true)));
        Assert.Contains("Checksum file", Assert.Throws<RestoreException>(() =>
            DatabaseRestore.Run(new RestoreOptions(ws.Db, junk, ws.Saved, Retry: true))).Message);
    }

    [Theory]
    [InlineData("marker-written")]
    [InlineData("current-saved")]
    [InlineData("backup-verified")]
    [InlineData("temp-restored")]
    [InlineData("links-revoked")]
    [InlineData("committed")]
    [InlineData("temp-verified")]
    [InlineData("old-moved")]
    [InlineData("swapped")]
    [InlineData("live-verified")]
    public async Task RestoreInterruptedAtAnyStep_LeavesServiceDown_AndDatabaseConsistent(string failAt)
    {
        using var ws = new Workspace();
        var (s, _) = await BuildHistoryAsync(ws);
        var liveBefore = DatabaseChecks.Sha256Hex(ws.Db);

        var ex = Assert.Throws<InvalidOperationException>(() => DatabaseRestore.Run(new RestoreOptions(ws.Db, s.BackupFile, ws.Saved,
            Hook: step => { if (step == failAt) throw new InvalidOperationException("simulated crash at " + step); })));
        Assert.Contains(failAt, ex.Message);

        // Public service is not reopened: the marker stays and the application refuses to start.
        Assert.True(File.Exists(RestoreLock.PathFor(ws.Db)));
        AssertServiceRefusesToStart(ws);

        // The working path holds either the untouched original or (never) a half-written file.
        Assert.Equal(liveBefore, DatabaseChecks.Sha256Hex(ws.Db));
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(ws.Db)!, "*.restoring-*"));
        Assert.Null(DatabaseChecks.Verify(ws.Db));

        // A plain re-run is refused until an operator passes --retry; with it the restore completes.
        Assert.Throws<RestoreException>(() => DatabaseRestore.Run(new RestoreOptions(ws.Db, s.BackupFile, ws.Saved)));
        var again = DatabaseRestore.Run(new RestoreOptions(ws.Db, s.BackupFile, ws.Saved, Retry: true, Now: DateTimeOffset.UtcNow.AddMinutes(1)));
        Assert.Equal(0, Scalar(ws.Db, DatabaseChecks.ActiveSql));
        Assert.True(again.Total > 0);
    }

    [Fact]
    public async Task SecondConcurrentRestore_IsRefused_WithoutTouchingAnything()
    {
        using var ws = new Workspace();
        var (s, _) = await BuildHistoryAsync(ws);
        var liveBefore = DatabaseChecks.Sha256Hex(ws.Db);

        using (new FileStream(Path.Combine(Path.GetDirectoryName(ws.Db)!, ".restore.lock"),
                   FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            var ex = Assert.Throws<RestoreException>(() => DatabaseRestore.Run(new RestoreOptions(ws.Db, s.BackupFile, ws.Saved)));
            Assert.Contains("Another restore", ex.Message);
        }
        Assert.Equal(liveBefore, DatabaseChecks.Sha256Hex(ws.Db));
        Assert.False(File.Exists(RestoreLock.PathFor(ws.Db)));
    }

    [Fact]
    public async Task Backup_WhileApplicationRunsWithLiveWal_IsCompleteSelfContainedAndIntact()
    {
        using var ws = new Workspace();
        using var app = new AppFactory(ws.Db);
        using var client = await app.CreateAdminClientAsync();
        var paths = new List<string>();
        for (var i = 0; i < 40; i++) paths.Add((await CreateAsync(client, "wal" + i)).Path);
        Assert.Equal(HttpStatusCode.Found, await OpenAsync(client, paths[0]));

        // The commits are in the WAL, not (all) in the main file: this is exactly what a plain file copy would miss.
        Assert.True(new FileInfo(ws.Db + "-wal").Length > 0, "expected a live, non-empty WAL");

        var backup = DatabaseBackup.Create(ws.Db, ws.Backups, DateTimeOffset.UtcNow);

        Assert.Equal(40, backup.Links);
        Assert.Equal(39, backup.Active);
        Assert.Null(DatabaseChecks.Verify(backup.Path));
        Assert.Equal(1, Scalar(backup.Path, DatabaseChecks.UsedSql));
        Assert.False(File.Exists(backup.Path + "-wal"));
        Assert.False(File.Exists(backup.Path + "-shm"));
        Assert.Equal(backup.Sha256, DatabaseChecks.Sha256Hex(backup.Path));
        Assert.StartsWith(backup.Sha256, File.ReadAllText(backup.Path + ".sha256"));
        // The application keeps working during and after the backup.
        Assert.Equal(HttpStatusCode.Found, await OpenAsync(client, paths[1]));
        // No raw token is stored in the backup (only SHA-256 hashes).
        var raw = await File.ReadAllTextAsync(backup.Path, System.Text.Encoding.Latin1);
        Assert.DoesNotContain(paths[2]["/r/".Length..], raw);
    }

    [Fact]
    public async Task Backup_IsPublishedAtomically_AndLeavesNoTempFilesOnFailure()
    {
        using var ws = new Workspace();
        var (_, _) = await BuildHistoryAsync(ws);
        var empty = new Workspace();
        using var _ = empty;

        Assert.Throws<InvalidOperationException>(() => DatabaseBackup.Create(ws.Db, empty.Backups, DateTimeOffset.UtcNow,
            hook: step => { if (step == "verified") throw new InvalidOperationException("crash before publish"); }));
        Assert.Empty(Directory.GetFileSystemEntries(empty.Backups)); // neither .db, .sha256 nor temp files

        Assert.Throws<FileNotFoundException>(() => DatabaseBackup.Create(Path.Combine(ws.Root, "missing.db"), empty.Backups, DateTimeOffset.UtcNow));
        Assert.False(File.Exists(Path.Combine(ws.Root, "missing.db"))); // no empty database is conjured up
        Assert.Empty(Directory.GetFileSystemEntries(empty.Backups));
    }

    [LinuxFact]
    public async Task Files_AreOwnerOnly_AndSqliteIsIntact()
    {
        using var ws = new Workspace();
        var (s, _) = await BuildHistoryAsync(ws);
        var result = DatabaseRestore.Run(new RestoreOptions(ws.Db, s.BackupFile, ws.Saved));

        foreach (var file in new[] { s.BackupFile, s.BackupFile + ".sha256", ws.Db, RestoreLock.PathFor(ws.Db),
                     Path.Combine(result.SavedDir, "redirects.db") })
            Assert.True(DatabaseChecks.IsOwnerOnly(file), $"{Path.GetFileName(file)} must not be group/world accessible");
        foreach (var dir in new[] { ws.Backups, ws.Saved, result.SavedDir })
            Assert.True(DatabaseChecks.IsOwnerOnly(dir), $"{Path.GetFileName(dir)} must be owner-only");
        Assert.Null(DatabaseChecks.Verify(ws.Db));
        Assert.Null(DatabaseChecks.Verify(s.BackupFile));
    }

    [Fact]
    public async Task Cli_BackupAndVerify_ReportWithoutLeakingContent_AndFailWithExitCode2()
    {
        using var ws = new Workspace();
        var (s, _) = await BuildHistoryAsync(ws);
        var stdout = new StringWriter();
        var stderr = new StringWriter();

        Assert.Equal(0, OpsCli.Run(["backup", "--db", ws.Db, "--out", Path.Combine(ws.Root, "cli")], stdout, stderr));
        var printed = stdout.ToString();
        Assert.Contains("BACKUP_FILE=", printed);
        Assert.DoesNotContain("example.com", printed);

        Assert.Equal(0, OpsCli.Run(["verify-db", "--db", s.BackupFile], new StringWriter(), stderr));
        Assert.Equal(2, OpsCli.Run(["verify-db", "--db", s.BackupFile, "--expect-no-active"], new StringWriter(), stderr)); // still has active links
        Assert.Equal(2, OpsCli.Run(["backup", "--db", Path.Combine(ws.Root, "nope.db"), "--out", ws.Backups], new StringWriter(), stderr));
        Assert.Equal(1, OpsCli.Run(["backup", "--db", ws.Db], new StringWriter(), stderr)); // --out missing

        Assert.Equal(0, OpsCli.Run(["restore-db", "--db", ws.Db, "--backup", s.BackupFile, "--saved-dir", ws.Saved], stdout, stderr));
        Assert.Equal(0, OpsCli.Run(["verify-db", "--db", ws.Db, "--expect-no-active"], new StringWriter(), stderr));
        Assert.Equal(0, OpsCli.Run(["restore-release", "--db", ws.Db], new StringWriter(), stderr));
        Assert.False(File.Exists(RestoreLock.PathFor(ws.Db)));
    }

    private static void AssertServiceRefusesToStart(Workspace ws)
    {
        using var app = new AppFactory(ws.Db);
        var ex = Record.Exception(() => app.CreateClient());
        Assert.NotNull(ex);
        Assert.Contains(Chain(ex), m => m.Contains(RestoreLock.FileName));
    }
}
