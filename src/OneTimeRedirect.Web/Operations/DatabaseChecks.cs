using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace OneTimeRedirect.Web.Operations;

/// <summary>Inspection helpers shared by backup, restore and verify-db. None of them log row contents.</summary>
public static class DatabaseChecks
{
    private static readonly string[] RequiredColumns =
        ["Id", "TokenHash", "TargetUrl", "Description", "CreatedAt", "ExpiresAt", "UsedAt", "RevokedAt"];

    public const string ActiveSql = "SELECT COUNT(*) FROM RedirectLinks WHERE UsedAt IS NULL AND RevokedAt IS NULL;";
    public const string TotalSql = "SELECT COUNT(*) FROM RedirectLinks;";
    public const string UsedSql = "SELECT COUNT(*) FROM RedirectLinks WHERE UsedAt IS NOT NULL;";

    public static string ConnectionString(string path, SqliteOpenMode mode) => new SqliteConnectionStringBuilder
    {
        DataSource = path, Mode = mode, Pooling = false, DefaultTimeout = 30
    }.ToString();

    public static SqliteConnection Open(string path, SqliteOpenMode mode)
    {
        var conn = new SqliteConnection(ConnectionString(path, mode));
        conn.Open();
        return conn;
    }

    /// <summary>Returns null when the file is an intact SQLite database with the expected schema, else a reason.</summary>
    public static string? Verify(string path)
    {
        if (!File.Exists(path)) return "file does not exist";
        try
        {
            using var conn = Open(path, SqliteOpenMode.ReadOnly);
            var rows = new List<string>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA integrity_check;";
                using var r = cmd.ExecuteReader();
                while (r.Read()) rows.Add(r.GetString(0));
            }
            if (rows.Count != 1 || rows[0] != "ok") return "integrity_check: " + string.Join("; ", rows.Take(5));

            var columns = new HashSet<string>();
            using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = "PRAGMA table_info(RedirectLinks);";
                using var r = cmd.ExecuteReader();
                while (r.Read()) columns.Add(r.GetString(1));
            }
            var missing = RequiredColumns.Where(c => !columns.Contains(c)).ToArray();
            return missing.Length == 0 ? null : "schema: RedirectLinks is missing " + string.Join(", ", missing);
        }
        catch (SqliteException ex)
        {
            // Never echo ex.Message: it can contain file paths.
            return $"not a usable SQLite database (SQLite error {ex.SqliteErrorCode})";
        }
    }

    public static long Scalar(string path, string sql)
    {
        using var conn = Open(path, SqliteOpenMode.ReadOnly);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public static string Sha256Hex(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs)).ToLowerInvariant();
    }

    public static void OwnerOnly(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        File.SetUnixFileMode(path, Directory.Exists(path)
            ? UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            : UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }

    /// <summary>True when only the owner has access (always true on Windows, where modes do not apply).</summary>
    public static bool IsOwnerOnly(string path) =>
        OperatingSystem.IsWindows() || (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite |
            UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) == 0;

    public static void Fsync(string path)
    {
        using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite);
        fs.Flush(flushToDisk: true);
    }

    public static void EnsureOwnerOnlyDirectory(string dir)
    {
        if (Directory.Exists(dir)) return;
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(dir);
        else Directory.CreateDirectory(dir, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
