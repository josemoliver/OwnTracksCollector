using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using OwnTracksCollector.Models;
using OwnTracksCollector.Services;
using OwnTracksCollector.Settings;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace OwnTracksCollector.Tests;

public sealed class DatabaseServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "owntracks-tests-" + Guid.NewGuid().ToString("N"));
    private string DbPath => Path.Combine(_dir, "sub", "test.db");   // parent folder does not exist yet

    private DatabaseService Create(bool removeDuplicates = false) =>
        new(MsOptions.Create(new DatabaseOptions { Path = DbPath, RemoveDuplicates = removeDuplicates }),
            NullLogger<DatabaseService>.Instance);

    private static LocationMessage Loc(long tst, string device = "phone", double lat = 18.4) =>
        new() { User = "jose", Device = device, Latitude = lat, Longitude = -66.1, Timestamp = tst, Battery = 80 };

    private static WaypointMessage Wp(string desc, long tst = 1_800_000_000, double lat = 18.4) =>
        new() { User = "jose", Device = "phone", Description = desc, Latitude = lat, Longitude = -66.1, Radius = 50, Timestamp = tst };

    private string Scalar(string sql)
    {
        using var c = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToString(cmd.ExecuteScalar())!;
    }

    private long Count(string sql) => long.Parse(Scalar(sql));

    private void Exec(string sql)
    {
        using var c = new SqliteConnection($"Data Source={DbPath};Pooling=False");
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void Initialize_creates_folder_schema_wal_and_unique_indexes()
    {
        using var db = Create();
        db.Initialize();

        Assert.Equal(1, Count("SELECT COUNT(*) FROM sqlite_master WHERE name='ux_loc_user_device_ts'"));
        Assert.Equal(1, Count("SELECT COUNT(*) FROM sqlite_master WHERE name='ux_wp_dedupe'"));
        Assert.Equal(0, Count("SELECT COUNT(*) FROM sqlite_master WHERE name='idx_loc_user_device_ts'"));
        Assert.Equal("wal", Scalar("PRAGMA journal_mode"));
    }

    [Fact]
    public void Batch_is_saved_and_duplicates_are_skipped()
    {
        using var db = Create();
        db.Initialize();

        var first = db.Save([Loc(100), Loc(101), Loc(100)], []);   // 100 repeated inside the batch
        var again = db.Save([Loc(101), Loc(102)], []);             // 101 repeated across batches

        Assert.Equal(new SaveResult(2, 1, 0), first);
        Assert.Equal(new SaveResult(1, 1, 0), again);
        Assert.Equal(3, Count("SELECT COUNT(*) FROM locations"));
    }

    [Fact]
    public void Same_timestamp_on_different_devices_is_not_a_duplicate()
    {
        using var db = Create();
        db.Initialize();

        var r = db.Save([Loc(100, "phone"), Loc(100, "tablet")], []);

        Assert.Equal(2, r.LocationsInserted);
    }

    [Fact]
    public void Resent_waypoint_replaces_instead_of_duplicating()
    {
        using var db = Create();
        db.Initialize();

        db.Save([], [Wp("Home")]);
        db.Save([], [Wp("Home (renamed)")]);
        db.Save([], [Wp("Work", lat: 18.5)]);   // different position = different waypoint

        Assert.Equal(2, Count("SELECT COUNT(*) FROM waypoints"));
        Assert.Equal(1, Count("SELECT COUNT(*) FROM waypoints WHERE description='Home (renamed)'"));
    }

    [Fact]
    public void Failed_batch_is_rolled_back_and_the_service_keeps_working()
    {
        using var db = Create();
        db.Initialize();

        // A NOT NULL violation (null user) in the middle of the batch
        var bad = new LocationMessage { User = null!, Device = "phone", Latitude = 1, Longitude = 1, Timestamp = 5 };
        Assert.ThrowsAny<Exception>(() => db.Save([Loc(10), bad, Loc(11)], []));

        Assert.Equal(0, Count("SELECT COUNT(*) FROM locations"));
        Assert.Equal(1, db.Save([Loc(12)], []).LocationsInserted);
    }

    [Fact]
    public void Existing_duplicates_keep_the_database_usable_and_are_not_deleted_by_default()
    {
        using (var seed = Create()) seed.Initialize();
        Exec("DROP INDEX ux_loc_user_device_ts");
        Exec("INSERT INTO locations (user, device, latitude, longitude, timestamp) VALUES ('jose','phone',1,1,50),('jose','phone',1,1,50)");

        using var db = Create();
        db.Initialize();

        Assert.Equal(2, Count("SELECT COUNT(*) FROM locations"));                                            // nothing deleted
        Assert.Equal(0, Count("SELECT COUNT(*) FROM sqlite_master WHERE name='ux_loc_user_device_ts'"));    // no unique index
        Assert.Equal(1, Count("SELECT COUNT(*) FROM sqlite_master WHERE name='idx_loc_user_device_ts'"));   // plain index instead
        Assert.Equal(1, db.Save([Loc(51)], []).LocationsInserted);                                           // still writes
    }

    [Fact]
    public void RemoveDuplicates_deletes_extras_keeps_oldest_and_builds_unique_index()
    {
        using (var seed = Create()) seed.Initialize();
        Exec("DROP INDEX ux_loc_user_device_ts");
        Exec("INSERT INTO locations (user, device, latitude, longitude, timestamp) VALUES ('jose','phone',1,1,50),('jose','phone',2,2,50),('jose','phone',3,3,60)");

        using var db = Create(removeDuplicates: true);
        db.Initialize();

        Assert.Equal(2, Count("SELECT COUNT(*) FROM locations"));
        Assert.Equal(1, Count("SELECT COUNT(*) FROM locations WHERE timestamp=50 AND latitude=1"));   // oldest kept
        Assert.Equal(1, Count("SELECT COUNT(*) FROM sqlite_master WHERE name='ux_loc_user_device_ts'"));
    }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
