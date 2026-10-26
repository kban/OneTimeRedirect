using Microsoft.Data.Sqlite;

namespace OneTimeRedirect.Web.Operations;

public sealed record BackupResult(string Path, string Sha256, long Links, long Active, long Bytes);

/// <summary>
/// Consistent online snapshot using the SQLite Online Backup API (SqliteConnection.BackupDatabase), never a file copy:
/// the live database may be in WAL mode and in use. The copy is written to a temporary file in the target directory,
/// converted to one self-contained file, verified, and only then renamed into place.
/// </summary>
public static class DatabaseBackup
{
    public static BackupResult Create(string dbPath, string outDir, DateTimeOffset now, Action<string>? hook = null)
    {
        dbPath = Path.GetFullPath(dbPath);
        outDir = Path.GetFullPath(outDir);
        if (!File.Exists(dbPath)) throw new FileNotFoundException("Source database not found (refusing to create an empty one).");
        DatabaseChecks.EnsureOwnerOnlyDirectory(outDir);

        var name = $"redirects-{now.UtcDateTime:yyyyMMdd'T'HHmmss'Z'}.db";
        var final = Path.Combine(outDir, name);
        var finalSum = final + ".sha256";
        if (File.Exists(final)) throw new IOException("A backup with this name already exists: " + name);

        var id = Guid.NewGuid().ToString("N");
        var tmp = Path.Combine(outDir, $".{name}.tmp-{id}");
        var tmpSum = Path.Combine(outDir, $".{name}.sha256.tmp-{id}");
        try
        {
            using (var source = DatabaseChecks.Open(dbPath, SqliteOpenMode.ReadOnly))
            using (var target = DatabaseChecks.Open(tmp, SqliteOpenMode.ReadWriteCreate))
            {
                source.BackupDatabase(target);
                hook?.Invoke("copied");
                // A copy of a WAL database carries the WAL flag; make it one self-contained file.
                using var cmd = target.CreateCommand();
                cmd.CommandText = "PRAGMA journal_mode=DELETE;";
                cmd.ExecuteNonQuery();
            }
            DatabaseChecks.OwnerOnly(tmp);
            DatabaseChecks.Fsync(tmp);

            if (DatabaseChecks.Verify(tmp) is { } problem)
                throw new InvalidDataException("Backup verification failed: " + problem);
            foreach (var side in new[] { tmp + "-wal", tmp + "-shm", tmp + "-journal" })
                if (File.Exists(side)) throw new InvalidDataException("Backup is not self-contained.");

            var sha = DatabaseChecks.Sha256Hex(tmp);
            File.WriteAllText(tmpSum, $"{sha}  {name}\n");
            DatabaseChecks.OwnerOnly(tmpSum);
            DatabaseChecks.Fsync(tmpSum);
            hook?.Invoke("verified");

            // Publish: checksum first, database last, so a visible *.db always has its checksum.
            File.Move(tmpSum, finalSum);
            File.Move(tmp, final);
            return new BackupResult(final, sha, DatabaseChecks.Scalar(final, DatabaseChecks.TotalSql),
                DatabaseChecks.Scalar(final, DatabaseChecks.ActiveSql), new FileInfo(final).Length);
        }
        catch
        {
            TryDelete(tmp, tmp + "-wal", tmp + "-shm", tmp + "-journal", tmpSum);
            if (!File.Exists(final)) TryDelete(finalSum);
            throw;
        }
    }

    private static void TryDelete(params string[] files)
    {
        foreach (var f in files)
            try { File.Delete(f); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
