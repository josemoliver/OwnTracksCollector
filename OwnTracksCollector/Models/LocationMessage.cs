namespace OwnTracksCollector.Models;

/// <summary>
/// Represents an OwnTracks location message (_type = "location").
/// See: https://owntracks.org/booklet/tech/json/
/// </summary>
public class LocationMessage
{
    /// <summary>MQTT topic user segment (owntracks/{user}/{device})</summary>
    public string User { get; set; } = string.Empty;

    /// <summary>MQTT topic device segment (owntracks/{user}/{device})</summary>
    public string Device { get; set; } = string.Empty;

    /// <summary>Latitude in decimal degrees (WGS 84)</summary>
    public double Latitude { get; set; }

    /// <summary>Longitude in decimal degrees (WGS 84)</summary>
    public double Longitude { get; set; }

    /// <summary>UNIX epoch timestamp (seconds since 1970-01-01T00:00:00Z)</summary>
    public long Timestamp { get; set; }

    /// <summary>Accuracy of location fix, in metres</summary>
    public double? Accuracy { get; set; }

    /// <summary>Altitude above mean sea level, in metres</summary>
    public double? Altitude { get; set; }

    /// <summary>Velocity (speed), in km/h</summary>
    public int? Velocity { get; set; }

    /// <summary>Device battery level, 0–100</summary>
    public int? Battery { get; set; }

    /// <summary>Short tracker ID (two-character label shown on the map)</summary>
    public string? TrackerId { get; set; }

    /// <summary>
    /// Trigger source of the location report:
    /// p = ping, c = circular region, b = beacon, r = reportLocation, u = manual, t = timer, v = vehicle
    /// </summary>
    public string? Trigger { get; set; }

    /// <summary>Connection status: w = WiFi, m = mobile, o = offline</summary>
    public string? Connection { get; set; }

    /// <summary>Original raw JSON payload stored for auditing / future re-processing</summary>
    public string? RawPayload { get; set; }
}
