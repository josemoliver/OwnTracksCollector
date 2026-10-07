namespace TrackViewer.Models;

public class WaypointRecord
{
    public long    Id          { get; set; }
    public string  User        { get; set; } = string.Empty;
    public string  Device      { get; set; } = string.Empty;
    public string? Description { get; set; }
    public double  Latitude    { get; set; }
    public double  Longitude   { get; set; }
    public int?    Radius      { get; set; }
    public long    Timestamp   { get; set; }
}
