namespace TrackViewer.Models;

public class ExportRequest
{
    public HashSet<DeviceIdentifier> Devices    { get; set; } = [];
    public DateTime?                 From       { get; set; }
    public DateTime?                 To         { get; set; }
    public HashSet<long>             WaypointIds{ get; set; } = [];
    public string                    Creator    { get; set; } = "TrackViewer";
}
