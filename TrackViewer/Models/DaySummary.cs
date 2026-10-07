namespace TrackViewer.Models;

/// <summary>Aggregate of one calendar day (in the report's time zone) that has location data.</summary>
public record DaySummary(
    DateOnly Date,
    int      Points,
    int      Devices,
    DateTime FirstFix,   // local time in the report's time zone
    DateTime LastFix);
