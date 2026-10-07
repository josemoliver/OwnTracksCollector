using Microsoft.Data.Sqlite;
using TrackViewer.Models;

namespace TrackViewer.Services;

/// <summary>
/// Builds the "only these devices" predicate as <c>(user = ? AND device = ?) OR ...</c>.
/// Unlike <c>(user || '/' || device) IN (...)</c>, this lets SQLite use the
/// (user, device, timestamp) index instead of scanning the whole table.
/// </summary>
internal static class DeviceSql
{
    public static string Clause(int count) =>
        " AND (" + string.Join(" OR ", Enumerable.Range(0, count).Select(i => $"(user = $u{i} AND device = $v{i})")) + ")";

    public static void AddParameters(SqliteCommand cmd, IEnumerable<DeviceIdentifier> devices)
    {
        var i = 0;
        foreach (var d in devices)
        {
            cmd.Parameters.AddWithValue($"$u{i}", d.User);
            cmd.Parameters.AddWithValue($"$v{i}", d.Device);
            i++;
        }
    }
}
