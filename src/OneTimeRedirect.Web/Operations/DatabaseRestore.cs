using Microsoft.Data.Sqlite;

namespace OneTimeRedirect.Web.Operations;

/// <summary>
/// Marker file next to the database. While it exists the application refuses to start and the systemd unit
/// does not start (ConditionPathExists). It is created when a restore begins and is removed only by the
/// restore script after every database check has passed, so a failed restore can never reopen service.
/// </summary>
public static class RestoreLock
{
    public const string FileName = "RESTORE_LOCK";

    public static string PathFor(string dbPath) => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(dbPath))!, FileName);

    public static void EnsureNotLocked(string dbPath)
    {
        if (File.Exists(PathFor(dbPath)))
            throw new InvalidOperationException(
                $"{FileName} exists next to the database: a restore is in progress or did not finish. Refusing to start (see the disaster recovery runbook).");
    }

    public static void Release(string dbPath) => File.Delete(PathFor(dbPath));
}

public sealed class RestoreException(string message, Exception? inner = null) : Exception(message, inner);

/// <param name="Hook">Called after each named step; throwing from it simulates a crash at that point (tests).</param>
public sealed record RestoreOptions(string DbPath, string BackupPath, string SavedRoot,
    bool Retry = false, bool AllowMissingChecksum = false, Action<string>? Hook = null, DateTimeOffset? Now = null);

public sealed record RestoreResult(long Total, long Used, long Revoked, string SavedDir);

/// <summary>
/// Restores a backup into the working database path. The backup is restored to a temporary file, every link that is
/// neither used nor revoked (including expired ones) is revoked in one transaction, and only the verified result
/// replaces the working database. The caller must have stopped the application beforehand.
/// </summary>
public static class DatabaseRestore
{
    private static readonly string[] Sidecars = ["", "-wal", "-shm"];

    public static RestoreResult Run(RestoreOptions o)
    {
        var db = Path.GetFullPath(o.DbPath);
        var backup = Path.GetFullPath(o.BackupPath);
        var dir = Path.GetDirectoryName(db)!;
        var now = o.Now ?? DateTimeOffset.UtcNow;
        var stamp = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmss'Z'");
        void Step(string name) => o.Hook?.Invoke(name);

        if (string.Equals(backup, db, StringComparison.OrdinalIgnoreCase))
            throw new RestoreException("The backup path must not be the working database.");
        if (!File.Exists(backup)) throw new RestoreException("Backup file not found.");
        Directory.CreateDirectory(dir);

        // Only one restore at a time; the OS releases this lock if the process dies.
        FileStream guard;
        try { guard = new FileStream(Path.Combine(dir, ".restore.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException ex) { throw new RestoreException("Another restore is running.", ex); }

        using (guard)
        {
            var marker = RestoreLock.PathFor(db);
            if (File.Exists(marker) && !o.Retry)
                throw new RestoreException($"{RestoreLock.FileName} exists: an earlier restore did not finish. Read the runbook, then re-run with --retry.");
            File.WriteAllText(marker, $"restore started {now:O}\n");
            DatabaseChecks.OwnerOnly(marker);
            Step("marker-written");

            // 1. Keep the current database, WAL and SHM for diagnostics (copies; originals stay until the swap).
            var savedDir = Path.Combine(Path.GetFullPath(o.SavedRoot), "restore-" + stamp);
            if (Directory.Exists(savedDir)) savedDir += "-" + Guid.NewGuid().ToString("N")[..6]; // a retry within the same second
            DatabaseChecks.EnsureOwnerOnlyDirectory(Path.GetFullPath(o.SavedRoot));
            DatabaseChecks.EnsureOwnerOnlyDirectory(savedDir);
            foreach (var s in Sidecars)
                if (File.Exists(db + s))
                {
                    var copy = Path.Combine(savedDir, Path.GetFileName(db) + s);
                    File.Copy(db + s, copy, overwrite: false);
                    DatabaseChecks.OwnerOnly(copy);
                }
            Step("current-saved");

            // 2. Verify the backup before touching anything.
            VerifyChecksum(backup, o.AllowMissingChecksum);
            if (DatabaseChecks.Verify(backup) is { } bad) throw new RestoreException("Backup is not usable: " + bad);
            Step("backup-verified");

            // 3-6. Restore to a temporary file, revoke, commit, verify it.
            var temp = $"{db}.restoring-{Guid.NewGuid():N}";
            long total, used, revoked;
            try
            {
                using (var source = DatabaseChecks.Open(backup, SqliteOpenMode.ReadOnly))
                using (var target = DatabaseChecks.Open(temp, SqliteOpenMode.ReadWriteCreate))
                    source.BackupDatabase(target);
                Step("temp-restored");

                using (var conn = DatabaseChecks.Open(temp, SqliteOpenMode.ReadWrite))
                {
                    Exec(conn, "PRAGMA journal_mode=DELETE;");
                    Exec(conn, "PRAGMA synchronous=FULL;");
                    using var tx = conn.BeginTransaction(deferred: false); // BEGIN IMMEDIATE
                    using (var cmd = conn.CreateCommand())
                    {
                        cmd.Transaction = tx;
                        // No expiry predicate on purpose: expired-but-unrevoked links are closed too.
                        cmd.CommandText = "UPDATE RedirectLinks SET RevokedAt = $now WHERE UsedAt IS NULL AND RevokedAt IS NULL;";
                        cmd.Parameters.AddWithValue("$now", now.ToUnixTimeMilliseconds());
                        revoked = cmd.ExecuteNonQuery();
                    }
                    using (var check = conn.CreateCommand())
                    {
                        check.Transaction = tx;
                        check.CommandText = DatabaseChecks.ActiveSql;
                        if (Convert.ToInt64(check.ExecuteScalar()) != 0)
                            throw new RestoreException("Active links remain after revocation; aborting.");
                    }
                    Step("links-revoked"); // still uncommitted: a failure here rolls everything back
                    tx.Commit();
                }
                Step("committed");

                DatabaseChecks.OwnerOnly(temp);
                DatabaseChecks.Fsync(temp);
                RequireClean(temp);
                total = DatabaseChecks.Scalar(temp, DatabaseChecks.TotalSql);
                used = DatabaseChecks.Scalar(temp, DatabaseChecks.UsedSql);
                Step("temp-verified");
            }
            catch
            {
                foreach (var s in Sidecars.Append("-journal")) TryDelete(temp + s);
                throw;
            }

            // 7. Swap with a way back.
            var moved = new List<(string From, string To)>();
            var published = false;
            void Rollback()
            {
                if (published)
                {
                    var failed = $"{db}.failed-{stamp}";
                    if (File.Exists(failed)) failed += "-" + Guid.NewGuid().ToString("N")[..6];
                    File.Move(db, failed);
                }
                for (var i = moved.Count - 1; i >= 0; i--) File.Move(moved[i].To, moved[i].From);
            }
            try
            {
                foreach (var s in Sidecars) // a stale -wal next to the new file would be replayed into it, so move all three
                    if (File.Exists(db + s))
                    {
                        var to = $"{db}{s}.replaced-{stamp}";
                        if (File.Exists(to)) to += "-" + Guid.NewGuid().ToString("N")[..6];
                        File.Move(db + s, to);
                        moved.Add((db + s, to));
                    }
                Step("old-moved");
                File.Move(temp, db);
                published = true;
                Step("swapped");

                RequireClean(db);
                if (DatabaseChecks.Scalar(db, DatabaseChecks.ActiveSql) != 0) throw new RestoreException("Working database still has active links.");
                if (!DatabaseChecks.IsOwnerOnly(db)) throw new RestoreException("Working database is not owner-only.");
                Step("live-verified");
            }
            catch
            {
                try { Rollback(); if (!published) foreach (var s in Sidecars.Append("-journal")) TryDelete(temp + s); }
                catch (Exception rb) { throw new RestoreException("Swap failed AND rollback failed; move the *.replaced-* files back by hand.", rb); }
                throw;
            }
            return new RestoreResult(total, used, revoked, savedDir);
        }
    }

    private static void RequireClean(string path)
    {
        if (DatabaseChecks.Verify(path) is { } problem) throw new RestoreException("Verification failed: " + problem);
        if (DatabaseChecks.Scalar(path, DatabaseChecks.ActiveSql) != 0) throw new RestoreException("Active links remain.");
    }

    private static void VerifyChecksum(string backup, bool allowMissing)
    {
        var sidecar = backup + ".sha256";
        if (!File.Exists(sidecar))
        {
            if (allowMissing) return;
            throw new RestoreException("Checksum file (.sha256) not found next to the backup.");
        }
        var expected = File.ReadAllText(sidecar).Trim().Split([' ', '\t'], 2)[0];
        if (!string.Equals(expected, DatabaseChecks.Sha256Hex(backup), StringComparison.OrdinalIgnoreCase))
            throw new RestoreException("Backup checksum mismatch: the file is corrupted or was modified.");
    }

    private static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    private static void TryDelete(string file)
    {
        try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
