namespace TrackViewer.Models;

public class TrackStatistics
{
    public int       TotalPoints   { get; set; }
    public int       TotalDevices  { get; set; }
    public double    TotalDistance { get; set; }  // km, Haversine
    public DateTime? EarliestFix   { get; set; }
    public DateTime? LatestFix     { get; set; }
}
