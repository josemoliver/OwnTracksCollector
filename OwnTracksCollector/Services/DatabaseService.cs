using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OwnTracksCollector.Models;
using OwnTracksCollector.Settings;

namespace OwnTracksCollector.Services;

/// <summary>
/// SQLite persistence for the collector.
///
/// Responsibilities:
///   - Creates the database file, schema and indexes (<see cref="Initialize"/>).
///   - Writes batches of locations and waypoints (<see cref="Save"/>).
///
/// One long-lived connection and two reusable INSERT commands are used, and each call writes
/// its whole batch in a single transaction. The connection runs in WAL mode with
/// <c>synchronous=NORMAL</c>: no fsync per commit, still safe against crashes for WAL, and
/// TrackViewer can read the same file while the collector writes. If a write fails, the
/// connection is discarded and reopened on the next call.
/// </summary>
public sealed class DatabaseService : IDatabaseService, IDisposable
{
    private readonly string _path;
    private readonly string _connectionString;
    private readonly bool   _removeDuplicates;
    private readonly ILogger<DatabaseService> _logger;

    private readonly object _gate = new();
    private SqliteConnection? _conn;
    private SqliteCommand?    _insertLocation;
    private SqliteCommand?    _insertWaypoint;

    public DatabaseService(IOptions<DatabaseOptions> options, ILogger<DatabaseService> logger)
    {
        _path             = options.Value.Path;
        _connectionString = $"Data Source={_path}";
        // Opt-in: deleting rows from an existing database is never done implicitly.
        _removeDuplicates = options.Value.RemoveDuplicates;
        _logger           = logger;
    }

    // ── Connection management ────────────────────────────────────────────────

    private SqliteConnection Connection()
    {
        if (_conn is not null) return _conn;

        var dir = Path.GetDirectoryName(Path.GetFullPath(_path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        var conn = new SqliteConnection(_connectionString);
        try
        {
            conn.Open();
            using (var pragma = conn.CreateCommand())
            {
                pragma.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;";
                pragma.ExecuteNonQuery();
            }

            _insertLocation = BuildInsertLocation(conn);
            _insertWaypoint = BuildInsertWaypoint(conn);
            _conn = conn;
            return conn;
        }
        catch
        {
            conn.Dispose();
            throw;
        }
    }

    private void ResetConnection()
    {
        _insertLocation?.Dispose();
        _insertWaypoint?.Dispose();
        _conn?.Dispose();
        _insertLocation = _insertWaypoint = null;
        _conn = null;
    }

    public void Dispose()
    {
        lock (_gate) ResetConnection();
    }

    // ── Schema ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates the database file (if absent) and runs idempotent DDL to set up
    /// the <c>locations</c> and <c>waypoints</c> tables plus their indexes.
    /// Safe to call on every startup — <c>CREATE TABLE IF NOT EXISTS</c> is a no-op
    /// when the schema already exists.
    /// </summary>
    public void Initialize()
    {
        lock (_gate)
        {
            var conn = Connection();

            using var cmd = conn.CreateCommand();
            cmd.CommandText = """
                CREATE TABLE IF NOT EXISTS locations (
                    id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    user        TEXT    NOT NULL,
                    device      TEXT    NOT NULL,
                    latitude    REAL    NOT NULL,
                    longitude   REAL    NOT NULL,
                    timestamp   INTEGER NOT NULL,
                    accuracy    REAL,
                    altitude    REAL,
                    velocity    INTEGER,
                    battery     INTEGER,
                    tracker_id  TEXT,
                    trigger     TEXT,
                    connection  TEXT,
                    raw_payload TEXT,
                    created_at  TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%SZ', 'now'))
                );

                CREATE INDEX IF NOT EXISTS idx_loc_user_device  ON locations (user, device);
                CREATE INDEX IF NOT EXISTS idx_loc_timestamp    ON locations (timestamp);

                CREATE TABLE IF NOT EXISTS waypoints (
                    id          INTEGER PRIMARY KEY AUTOINCREMENT,
                    user        TEXT    NOT NULL,
                    device      TEXT    NOT NULL,
                    description TEXT,
                    latitude    REAL    NOT NULL,
                    longitude   REAL    NOT NULL,
                    radius      INTEGER,
                    timestamp   INTEGER NOT NULL,
                    raw_payload TEXT,
                    created_at  TEXT    NOT NULL DEFAULT (strftime('%Y-%m-%dT%H:%M:%SZ', 'now'))
                );

                CREATE INDEX IF NOT EXISTS idx_wp_user_device ON waypoints (user, device);
                CREATE INDEX IF NOT EXISTS idx_wp_timestamp   ON waypoints (timestamp);
                """;
            cmd.ExecuteNonQuery();

            // Locations: one fix per device per second. Waypoints: the same waypoint re-sent
            // (e.g. a bulk re-export) is replaced, not duplicated.
            EnsureDedupeIndex(conn, "locations", "user, device, timestamp",
                "ux_loc_user_device_ts", "idx_loc_user_device_ts");
            EnsureDedupeIndex(conn, "waypoints", "user, device, timestamp, latitude, longitude",
                "ux_wp_dedupe", "idx_wp_user_device_ts", fallbackColumns: "user, device, timestamp");

            _logger.LogInformation("Database initialized at: {ConnectionString}", _connectionString);
        }
    }

    /// <summary>
    /// Makes sure rows are unique on <paramref name="columns"/> by creating a UNIQUE index.
    /// If the table already holds duplicates the index cannot be built: they are removed only
    /// when <c>Database:RemoveDuplicates</c> is true (keeping the oldest row of each group);
    /// otherwise a warning is logged and a plain index is created so queries stay fast.
    /// Inserts still skip new duplicates either way.
    /// </summary>
    private void EnsureDedupeIndex(SqliteConnection conn, string table, string columns,
                                   string uniqueIndex, string plainIndex, string? fallbackColumns = null)
    {
        if (ScalarLong(conn, $"SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='{uniqueIndex}'") > 0)
            return;

        var groups = ScalarLong(conn,
            $"SELECT COUNT(*) FROM (SELECT 1 FROM {table} GROUP BY {columns} HAVING COUNT(*) > 1)");

        if (groups > 0 && _removeDuplicates)
        {
            using var tx = conn.BeginTransaction();
            using var del = conn.CreateCommand();
            del.Transaction = tx;
            del.CommandText =
                $"DELETE FROM {table} WHERE id NOT IN (SELECT MIN(id) FROM {table} GROUP BY {columns})";
            var removed = del.ExecuteNonQuery();
            tx.Commit();
            _logger.LogWarning("Removed {Removed} duplicate row(s) from {Table} ({Groups} duplicated groups).",
                removed, table, groups);
            groups = 0;
        }

        using var cmd = conn.CreateCommand();
        if (groups == 0)
        {
            cmd.CommandText = $"""
                CREATE UNIQUE INDEX IF NOT EXISTS {uniqueIndex} ON {table} ({columns});
                DROP INDEX IF EXISTS {plainIndex};
                """;
            cmd.ExecuteNonQuery();
            _logger.LogInformation("Created unique index {Index} on {Table}.", uniqueIndex, table);
        }
        else
        {
            cmd.CommandText = $"CREATE INDEX IF NOT EXISTS {plainIndex} ON {table} ({fallbackColumns ?? columns})";
            cmd.ExecuteNonQuery();
            _logger.LogWarning(
                "{Table} contains {Groups} group(s) of duplicate rows, so the unique index was not created. " +
                "Set Database:RemoveDuplicates=true once to delete them (the oldest row of each group is kept).",
                table, groups);
        }
    }

    private static long ScalarLong(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    // ── Writes ───────────────────────────────────────────────────────────────

    public SaveResult Save(IReadOnlyList<LocationMessage> locations, IReadOnlyList<WaypointMessage> waypoints)
    {
        if (locations.Count == 0 && waypoints.Count == 0)
            return default;

        lock (_gate)
        {
            try
            {
                var conn = Connection();
                using var tx = conn.BeginTransaction();
                _insertLocation!.Transaction = tx;
                _insertWaypoint!.Transaction = tx;

                var inserted = 0;
                foreach (var m in locations)
                {
                    BindLocation(_insertLocation, m);
                    inserted += _insertLocation.ExecuteNonQuery();   // 0 when INSERT OR IGNORE skipped a duplicate
                }

                foreach (var w in waypoints)
                {
                    BindWaypoint(_insertWaypoint, w);
                    _insertWaypoint.ExecuteNonQuery();
                }

                tx.Commit();
                return new SaveResult(inserted, locations.Count - inserted, waypoints.Count);
            }
            catch
            {
                // Don't reuse a connection or commands that may be mid-transaction
                ResetConnection();
                throw;
            }
        }
    }

    private static SqliteCommand BuildInsertLocation(SqliteConnection conn)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO locations
                (user, device, latitude, longitude, timestamp,
                 accuracy, altitude, velocity, battery, tracker_id,
                 trigger, connection, raw_payload)
            VALUES
                ($user, $device, $lat, $lon, $tst,
                 $acc, $alt, $vel, $batt, $tid,
                 $trigger, $conn, $raw)
            """;
        cmd.Parameters.Add("$user",    SqliteType.Text);
        cmd.Parameters.Add("$device",  SqliteType.Text);
        cmd.Parameters.Add("$lat",     SqliteType.Real);
        cmd.Parameters.Add("$lon",     SqliteType.Real);
        cmd.Parameters.Add("$tst",     SqliteType.Integer);
        cmd.Parameters.Add("$acc",     SqliteType.Real);
        cmd.Parameters.Add("$alt",     SqliteType.Real);
        cmd.Parameters.Add("$vel",     SqliteType.Integer);
        cmd.Parameters.Add("$batt",    SqliteType.Integer);
        cmd.Parameters.Add("$tid",     SqliteType.Text);
        cmd.Parameters.Add("$trigger", SqliteType.Text);
        cmd.Parameters.Add("$conn",    SqliteType.Text);
        cmd.Parameters.Add("$raw",     SqliteType.Text);
        return cmd;
    }

    private static void BindLocation(SqliteCommand cmd, LocationMessage m)
    {
        var p = cmd.Parameters;
        p["$user"].Value    = m.User;
        p["$device"].Value  = m.Device;
        p["$lat"].Value     = m.Latitude;
        p["$lon"].Value     = m.Longitude;
        p["$tst"].Value     = m.Timestamp;
        p["$acc"].Value     = (object?)m.Accuracy   ?? DBNull.Value;
        p["$alt"].Value     = (object?)m.Altitude   ?? DBNull.Value;
        p["$vel"].Value     = (object?)m.Velocity   ?? DBNull.Value;
        p["$batt"].Value    = (object?)m.Battery    ?? DBNull.Value;
        p["$tid"].Value     = (object?)m.TrackerId  ?? DBNull.Value;
        p["$trigger"].Value = (object?)m.Trigger    ?? DBNull.Value;
        p["$conn"].Value    = (object?)m.Connection ?? DBNull.Value;
        p["$raw"].Value     = (object?)m.RawPayload ?? DBNull.Value;
    }

    private static SqliteCommand BuildInsertWaypoint(SqliteConnection conn)
    {
        var cmd = conn.CreateCommand();
        cmd.CommandText = """
            INSERT OR REPLACE INTO waypoints
                (user, device, description, latitude, longitude, radius, timestamp, raw_payload)
            VALUES
                ($user, $device, $desc, $lat, $lon, $rad, $tst, $raw)
            """;
        cmd.Parameters.Add("$user",   SqliteType.Text);
        cmd.Parameters.Add("$device", SqliteType.Text);
        cmd.Parameters.Add("$desc",   SqliteType.Text);
        cmd.Parameters.Add("$lat",    SqliteType.Real);
        cmd.Parameters.Add("$lon",    SqliteType.Real);
        cmd.Parameters.Add("$rad",    SqliteType.Integer);
        cmd.Parameters.Add("$tst",    SqliteType.Integer);
        cmd.Parameters.Add("$raw",    SqliteType.Text);
        return cmd;
    }

    private static void BindWaypoint(SqliteCommand cmd, WaypointMessage m)
    {
        var p = cmd.Parameters;
        p["$user"].Value   = m.User;
        p["$device"].Value = m.Device;
        p["$desc"].Value   = (object?)m.Description ?? DBNull.Value;
        p["$lat"].Value    = m.Latitude;
        p["$lon"].Value    = m.Longitude;
        p["$rad"].Value    = (object?)m.Radius      ?? DBNull.Value;
        p["$tst"].Value    = m.Timestamp;
        p["$raw"].Value    = (object?)m.RawPayload  ?? DBNull.Value;
    }
}
