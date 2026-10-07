using Microsoft.Data.Sqlite;
using TrackViewer.Models;

namespace TrackViewer.Services;

public class ReportQueryService
{
    // Rows are pre-aggregated into 15-minute buckets in SQL. Every UTC offset is a
    // multiple of 15 minutes, so a bucket never straddles local midnight and the
    // per-day grouping stays correct across DST changes without scanning every row.
    private const int BucketSeconds = 900;

    private readonly string _connectionString;

    public ReportQueryService(IConfiguration config)
    {
        var path = config["Database:Path"]
            ?? throw new InvalidOperationException("Database:Path is not configured.");
        _connectionString = $"Data Source={path}";
    }

    /// <summary>
    /// Days that have location data between <paramref name="from"/> and <paramref name="to"/>
    /// (both inclusive, as calendar dates in <paramref name="tz"/>). Null bounds are open-ended.
    /// </summary>
    public async Task<IReadOnlyList<DaySummary>> GetDailySummaryAsync(
        IReadOnlyCollection<DeviceIdentifier> devices, DateTime? from, DateTime? to, TimeZoneInfo tz)
    {
        var bounds = new TrackFilter { TimeZone = tz };
        var sql = new System.Text.StringBuilder("""
            SELECT user, device, timestamp / $bucket AS bucket,
                   COUNT(*), MIN(timestamp), MAX(timestamp)
            FROM   locations
            WHERE  1=1
            """);

        if (devices.Count > 0)
            sql.Append(DeviceSql.Clause(devices.Count));
        if (from.HasValue) sql.Append(" AND timestamp >= $from");
        if (to.HasValue)   sql.Append(" AND timestamp < $to");
        sql.Append(" GROUP BY user, device, bucket");

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sql.ToString();
        cmd.Parameters.AddWithValue("$bucket", BucketSeconds);

        DeviceSql.AddParameters(cmd, devices);
        if (from.HasValue) cmd.Parameters.AddWithValue("$from", bounds.ToUnixSeconds(from.Value.Date));
        if (to.HasValue)   cmd.Parameters.AddWithValue("$to",   bounds.ToUnixSeconds(to.Value.Date.AddDays(1)));

        var days = new Dictionary<DateOnly, (int Points, HashSet<string> Devs, long First, long Last)>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            var key    = $"{reader.GetString(0)}/{reader.GetString(1)}";
            var bucket = reader.GetInt64(2);
            var count  = reader.GetInt32(3);
            var first  = reader.GetInt64(4);
            var last   = reader.GetInt64(5);

            var utc  = DateTimeOffset.FromUnixTimeSeconds(bucket * BucketSeconds).UtcDateTime;
            var date = DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(utc, tz));

            if (days.TryGetValue(date, out var acc))
            {
                acc.Devs.Add(key);
                days[date] = (acc.Points + count, acc.Devs, Math.Min(acc.First, first), Math.Max(acc.Last, last));
            }
            else
            {
                days[date] = (count, [key], first, last);
            }
        }

        DateTime Local(long unix) =>
            TimeZoneInfo.ConvertTimeFromUtc(DateTimeOffset.FromUnixTimeSeconds(unix).UtcDateTime, tz);

        return days
            .OrderBy(kv => kv.Key)
            .Select(kv => new DaySummary(kv.Key, kv.Value.Points, kv.Value.Devs.Count,
                                         Local(kv.Value.First), Local(kv.Value.Last)))
            .ToList();
    }
}
