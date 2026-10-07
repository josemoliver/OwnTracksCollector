namespace OwnTracksCollector.Models;

/// <summary>
/// Represents an OwnTracks waypoint message (_type = "waypoint").
/// Also used for individual entries in a bulk "_type": "waypoints" payload.
/// See: https://owntracks.org/booklet/tech/json/
/// </summary>
public class WaypointMessage
{
    /// <summary>MQTT topic user segment (owntracks/{user}/{device})</summary>
    public string User { get; set; } = string.Empty;

    /// <summary>MQTT topic device segment (owntracks/{user}/{device})</summary>
    public string Device { get; set; } = string.Empty;

    /// <summary>Waypoint description / name</summary>
    public string? Description { get; set; }

    /// <summary>Latitude in decimal degrees (WGS 84)</summary>
    public double Latitude { get; set; }

    /// <summary>Longitude in decimal degrees (WGS 84)</summary>
    public double Longitude { get; set; }

    /// <summary>Radius of the geofence in metres</summary>
    public int? Radius { get; set; }

    /// <summary>UNIX epoch timestamp when the waypoint was created/updated</summary>
    public long Timestamp { get; set; }

    /// <summary>Raw JSON payload</summary>
    public string? RawPayload { get; set; }
}
