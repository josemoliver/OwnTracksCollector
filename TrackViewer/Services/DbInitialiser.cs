using Microsoft.Data.Sqlite;

namespace TrackViewer;

public static class DbInitialiser
{
    public static void EnableWal(string path)
    {
        using var conn = new SqliteConnection($"Data Source={path}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "PRAGMA journal_mode=WAL;";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Adds the composite index the viewer's queries rely on (device filter + time range + ORDER BY).
    /// Best effort: the first run on a large table can take a moment, and a read-only DB is tolerated.
    /// </summary>
    public static void EnsureIndexes(string path, ILogger logger)
    {
        try
        {
            using var conn = new SqliteConnection($"Data Source={path}");
            conn.Open();
            using var cmd = conn.CreateCommand();
            // The collector may already have created a unique index covering the same columns
            // (ux_*); only add a plain one when neither exists, so the table isn't indexed twice.
            cmd.CommandText = """
                SELECT COUNT(*) FROM sqlite_master
                WHERE name IN ('ux_loc_user_device_ts', 'idx_loc_user_device_ts');
                """;
            if (Convert.ToInt64(cmd.ExecuteScalar()) == 0)
            {
                cmd.CommandText =
                    "CREATE INDEX IF NOT EXISTS idx_loc_user_device_ts ON locations (user, device, timestamp)";
                cmd.ExecuteNonQuery();
            }

            cmd.CommandText = """
                SELECT COUNT(*) FROM sqlite_master
                WHERE name IN ('ux_wp_dedupe', 'idx_wp_user_device_ts');
                """;
            if (Convert.ToInt64(cmd.ExecuteScalar()) == 0)
            {
                cmd.CommandText =
                    "CREATE INDEX IF NOT EXISTS idx_wp_user_device_ts ON waypoints (user, device, timestamp)";
                cmd.ExecuteNonQuery();
            }
        }
        catch (SqliteException ex)
        {
            logger.LogWarning(ex, "Could not create query indexes; queries may be slower.");
        }
    }
}
