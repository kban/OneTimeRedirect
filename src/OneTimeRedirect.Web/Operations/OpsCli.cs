namespace OneTimeRedirect.Web.Operations;

/// <summary>
/// Operator commands that run without starting the web host (so they never serve requests):
/// backup, restore-db, restore-release, verify-db. Output goes to stdout/stderr and never contains tokens or URLs.
/// </summary>
public static class OpsCli
{
    public static bool IsOpsCommand(string[] args) =>
        args is [("backup" or "restore-db" or "restore-release" or "verify-db"), ..];

    public static int Run(string[] args, TextWriter? stdout = null, TextWriter? stderr = null)
    {
        var @out = stdout ?? Console.Out;
        var err = stderr ?? Console.Error;
        var opts = Parse(args.Skip(1).ToArray());
        string Need(string key) => opts.TryGetValue(key, out var v) && v.Length > 0 ? v : throw new ArgumentException($"--{key} is required");
        var db = opts.TryGetValue("db", out var d) && d.Length > 0 ? d : Environment.GetEnvironmentVariable("REDIRECT_DB_PATH") ?? "";

        try
        {
            if (db.Length == 0) throw new ArgumentException("--db (or REDIRECT_DB_PATH) is required");
            switch (args[0])
            {
                case "backup":
                {
                    var r = DatabaseBackup.Create(db, Need("out"), DateTimeOffset.UtcNow);
                    @out.WriteLine($"BACKUP_FILE={r.Path}");
                    @out.WriteLine($"SHA256={r.Sha256}");
                    @out.WriteLine($"LINKS={r.Links} ACTIVE={r.Active} BYTES={r.Bytes}");
                    return 0;
                }
                case "restore-db":
                {
                    var r = DatabaseRestore.Run(new RestoreOptions(db, Need("backup"), Need("saved-dir"),
                        Retry: opts.ContainsKey("retry"), AllowMissingChecksum: opts.ContainsKey("allow-missing-checksum")));
                    @out.WriteLine($"RESTORED links={r.Total} used={r.Used} revoked_on_restore={r.Revoked} saved_dir={r.SavedDir}");
                    return 0;
                }
                case "restore-release":
                    RestoreLock.Release(db);
                    @out.WriteLine("RESTORE_LOCK released");
                    return 0;
                case "verify-db":
                {
                    if (DatabaseChecks.Verify(db) is { } problem) throw new RestoreException(problem);
                    var active = DatabaseChecks.Scalar(db, DatabaseChecks.ActiveSql);
                    @out.WriteLine($"OK links={DatabaseChecks.Scalar(db, DatabaseChecks.TotalSql)} active={active}");
                    if (opts.ContainsKey("expect-no-active") && active != 0)
                        throw new RestoreException("Active links present, none expected.");
                    return 0;
                }
            }
            return 1;
        }
        catch (ArgumentException ex)
        {
            err.WriteLine("usage error: " + ex.Message);
            return 1;
        }
        catch (Exception ex) when (ex is RestoreException or IOException or InvalidDataException or UnauthorizedAccessException
                                       or Microsoft.Data.Sqlite.SqliteException)
        {
            // Type and message only: no stack traces (paths are fine, tokens/URLs are never part of these messages).
            err.WriteLine($"FAILED: {ex.GetType().Name}: {(ex is Microsoft.Data.Sqlite.SqliteException s ? "SQLite error " + s.SqliteErrorCode : ex.Message)}");
            return 2;
        }
    }

    private static Dictionary<string, string> Parse(string[] args)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var i = 0; i < args.Length; i++)
        {
            if (!args[i].StartsWith("--", StringComparison.Ordinal)) throw new ArgumentException("unexpected argument " + args[i]);
            var key = args[i][2..];
            map[key] = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[++i] : "";
        }
        return map;
    }
}
