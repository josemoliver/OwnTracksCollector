using System.Text;
using Microsoft.Data.Sqlite;
using TrackViewer.Models;

namespace TrackViewer.Services;

public class LocationQueryService
{
    private readonly string _connectionString;
    private readonly int    _maxRows;
    private readonly double _tripThresholdMinutes;

    public LocationQueryService(IConfiguration config)
    {
        var path = config["Database:Path"]
            ?? throw new InvalidOperationException("Database:Path is not configured.");
        _connectionString     = $"Data Source={path}";
        _maxRows              = config.GetValue("Query:MaxLocationRows", 50_000);
        _tripThresholdMinutes = config.GetValue("Query:TripSegmentThresholdMinutes", 30.0);
    }

    public double TripThresholdMinutes => _tripThresholdMinutes;

    // ── Device list ──────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<DeviceIdentifier>> GetDevicesAsync()
    {
        const string sql = """
            SELECT DISTINCT user, device
            FROM   locations
            ORDER  BY user, device
            """;

        var result = new List<DeviceIdentifier>();
        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
            result.Add(new DeviceIdentifier(reader.GetString(0), reader.GetString(1)));
        return result;
    }

    // ── Location rows ────────────────────────────────────────────────────────

    public async Task<(IReadOnlyList<LocationRecord> Records, bool Capped)> GetLocationsAsync(TrackFilter filter)
    {
        var devices = filter.SelectedDevices.ToList();
        var sb      = new StringBuilder();
        sb.Append("""
            SELECT id, user, device, latitude, longitude, timestamp,
                   accuracy, altitude, velocity, battery, tracker_id, trigger, connection,
                   CASE WHEN raw_payload LIKE '%"_venue"%' AND json_valid(raw_payload)
                        THEN CAST(json_extract(raw_payload, '$._venue') AS TEXT) END
            FROM   locations
            WHERE  1=1
            """);

        if (devices.Count > 0)
            sb.Append(DeviceSql.Clause(devices.Count));
        if (filter.From.HasValue) sb.Append(" AND timestamp >= $from");
        if (filter.To.HasValue)   sb.Append(" AND timestamp <= $to");
        if (filter.MinLat.HasValue)
        {
            sb.Append(" AND latitude  BETWEEN $minLat AND $maxLat");
            sb.Append(" AND longitude BETWEEN $minLon AND $maxLon");
        }
        sb.Append(" ORDER BY user, device, timestamp");
        sb.Append($" LIMIT {_maxRows + 1}");  // fetch one extra to detect capping

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sb.ToString();

        DeviceSql.AddParameters(cmd, devices);
        if (filter.From.HasValue)
            cmd.Parameters.AddWithValue("$from", filter.ToUnixSeconds(filter.From.Value));
        if (filter.To.HasValue)
            cmd.Parameters.AddWithValue("$to", filter.ToUnixSeconds(filter.To.Value));
        if (filter.MinLat.HasValue)
        {
            cmd.Parameters.AddWithValue("$minLat", filter.MinLat);
            cmd.Parameters.AddWithValue("$maxLat", filter.MaxLat);
            cmd.Parameters.AddWithValue("$minLon", filter.MinLon);
            cmd.Parameters.AddWithValue("$maxLon", filter.MaxLon);
        }

        var rows = new List<LocationRecord>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add(new LocationRecord
            {
                Id         = reader.GetInt64(0),
                User       = reader.GetString(1),
                Device     = reader.GetString(2),
                Latitude   = reader.GetDouble(3),
                Longitude  = reader.GetDouble(4),
                Timestamp  = reader.GetInt64(5),
                Accuracy   = reader.IsDBNull(6)  ? null : reader.GetDouble(6),
                Altitude   = reader.IsDBNull(7)  ? null : reader.GetDouble(7),
                Velocity   = reader.IsDBNull(8)  ? null : reader.GetInt32(8),
                Battery    = reader.IsDBNull(9)  ? null : reader.GetInt32(9),
                TrackerId  = reader.IsDBNull(10) ? null : reader.GetString(10),
                Trigger    = reader.IsDBNull(11) ? null : reader.GetString(11),
                Connection = reader.IsDBNull(12) ? null : reader.GetString(12),
                Venue      = reader.IsDBNull(13) ? null : reader.GetString(13),
            });
        }

        var capped = rows.Count > _maxRows;
        if (capped) rows.RemoveAt(rows.Count - 1);
        return (rows, capped);
    }

    // ── Statistics ────────────────────────────────────────────────────────────

    public TrackStatistics ComputeStatistics(IReadOnlyList<LocationRecord> records)
    {
        if (records.Count == 0)
            return new TrackStatistics();

        var stats = new TrackStatistics
        {
            TotalPoints  = records.Count,
            TotalDevices = records.Select(r => $"{r.User}/{r.Device}").Distinct().Count(),
            EarliestFix  = DateTimeOffset.FromUnixTimeSeconds(records.Min(r => r.Timestamp)).UtcDateTime,
            LatestFix    = DateTimeOffset.FromUnixTimeSeconds(records.Max(r => r.Timestamp)).UtcDateTime,
        };

        // Haversine total distance per device track
        double totalKm = 0;
        var grouped = records.GroupBy(r => (r.User, r.Device));
        foreach (var group in grouped)
        {
            var sorted = group.OrderBy(r => r.Timestamp).ToList();
            for (var i = 1; i < sorted.Count; i++)
                totalKm += Haversine(sorted[i - 1].Latitude, sorted[i - 1].Longitude,
                                     sorted[i].Latitude,     sorted[i].Longitude);
        }
        stats.TotalDistance = Math.Round(totalKm, 1);
        return stats;
    }

    private static double Haversine(double lat1, double lon1, double lat2, double lon2)
    {
        const double R = 6371.0;
        var dLat = (lat2 - lat1) * Math.PI / 180;
        var dLon = (lon2 - lon1) * Math.PI / 180;
        var a    = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                 + Math.Cos(lat1 * Math.PI / 180) * Math.Cos(lat2 * Math.PI / 180)
                 * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        return R * 2 * Math.Atan2(Math.Sqrt(a), Math.Sqrt(1 - a));
    }
}
