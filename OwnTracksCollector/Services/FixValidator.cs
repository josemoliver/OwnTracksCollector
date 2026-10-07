namespace OwnTracksCollector.Services;

/// <summary>
/// Sanity checks for incoming coordinates and timestamps. Returns a human-readable
/// reason when a value should be rejected, or null when it is acceptable.
/// </summary>
public static class FixValidator
{
    // A device with an unset clock reports 1970; nothing before this is a real fix.
    private static readonly long MinTimestamp =
        new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero).ToUnixTimeSeconds();

    // Allow for modest clock skew between the device and this machine.
    private static readonly TimeSpan MaxFutureSkew = TimeSpan.FromHours(24);

    public static string? Validate(double latitude, double longitude, long timestamp, DateTimeOffset? now = null)
    {
        if (!double.IsFinite(latitude) || !double.IsFinite(longitude))
            return "coordinates are not finite numbers";

        if (latitude is < -90 or > 90)
            return $"latitude {latitude} is outside -90..90";

        if (longitude is < -180 or > 180)
            return $"longitude {longitude} is outside -180..180";

        // Exactly 0,0 ("Null Island") is what devices report when they have no fix.
        if (latitude == 0 && longitude == 0)
            return "coordinates are exactly 0,0 (no GPS fix)";

        if (timestamp < MinTimestamp)
            return $"timestamp {timestamp} is before 2000-01-01 (device clock not set?)";

        var limit = (now ?? DateTimeOffset.UtcNow).Add(MaxFutureSkew).ToUnixTimeSeconds();
        if (timestamp > limit)
            return $"timestamp {timestamp} is more than {MaxFutureSkew.TotalHours:0} h in the future";

        return null;
    }
}
