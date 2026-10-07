namespace TrackViewer.Models;

public class TrackFilter
{
    public HashSet<DeviceIdentifier> SelectedDevices { get; set; } = [];
    public DateTime? From { get; set; }
    public DateTime? To   { get; set; }

    /// <summary>Time zone in which From/To are expressed. Defaults to UTC.</summary>
    public TimeZoneInfo TimeZone { get; set; } = TimeZoneInfo.Utc;

    public double? MinLat { get; set; }
    public double? MaxLat { get; set; }
    public double? MinLon { get; set; }
    public double? MaxLon { get; set; }
    public bool ShowTracks    { get; set; } = true;
    public bool ShowPoints    { get; set; } = false;
    public bool ShowWaypoints { get; set; } = true;
    public bool ShowHeatmap   { get; set; } = false;

    /// <summary>Converts a From/To value (in <see cref="TimeZone"/>) to Unix seconds.</summary>
    public long ToUnixSeconds(DateTime value)
    {
        var unspecified = DateTime.SpecifyKind(value, DateTimeKind.Unspecified);
        if (TimeZone.IsInvalidTime(unspecified))   // skipped by a DST jump
            unspecified = unspecified.AddHours(1);
        return new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(unspecified, TimeZone)).ToUnixTimeSeconds();
    }
}
