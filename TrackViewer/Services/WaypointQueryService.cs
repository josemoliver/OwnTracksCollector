using System.Text;
using Microsoft.Data.Sqlite;
using TrackViewer.Models;

namespace TrackViewer.Services;

public class WaypointQueryService
{
    private readonly string _connectionString;

    public WaypointQueryService(IConfiguration config)
    {
        var path = config["Database:Path"]
            ?? throw new InvalidOperationException("Database:Path is not configured.");
        _connectionString = $"Data Source={path}";
    }

    public async Task<IReadOnlyList<WaypointRecord>> GetWaypointsAsync(
        IEnumerable<DeviceIdentifier>? devices = null)
    {
        var deviceList = devices?.ToList() ?? [];

        var sb = new StringBuilder("""
            SELECT id, user, device, description, latitude, longitude, radius, timestamp
            FROM   waypoints
            WHERE  1=1
            """);

        if (deviceList.Count > 0)
            sb.Append(DeviceSql.Clause(deviceList.Count));
        sb.Append(" ORDER BY user, device, timestamp");

        await using var conn = new SqliteConnection(_connectionString);
        await conn.OpenAsync();
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = sb.ToString();

        DeviceSql.AddParameters(cmd, deviceList);

        var result = new List<WaypointRecord>();
        await using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            result.Add(new WaypointRecord
            {
                Id          = reader.GetInt64(0),
                User        = reader.GetString(1),
                Device      = reader.GetString(2),
                Description = reader.IsDBNull(3) ? null : reader.GetString(3),
                Latitude    = reader.GetDouble(4),
                Longitude   = reader.GetDouble(5),
                Radius      = reader.IsDBNull(6) ? null : reader.GetInt32(6),
                Timestamp   = reader.GetInt64(7),
            });
        }
        return result;
    }
}
