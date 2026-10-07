namespace TrackViewer.Models;

public class LocationRecord
{
    public long    Id         { get; set; }
    public string  User       { get; set; } = string.Empty;
    public string  Device     { get; set; } = string.Empty;
    public double  Latitude   { get; set; }
    public double  Longitude  { get; set; }
    public long    Timestamp  { get; set; }
    public double? Accuracy   { get; set; }
    public double? Altitude   { get; set; }
    public int?    Velocity   { get; set; }
    public int?    Battery    { get; set; }
    public string? TrackerId  { get; set; }
    public string? Trigger    { get; set; }
    public string? Connection { get; set; }

    /// <summary>Optional <c>_venue</c> field from the raw JSON payload (e.g. imported check-ins).</summary>
    public string? Venue      { get; set; }
}
