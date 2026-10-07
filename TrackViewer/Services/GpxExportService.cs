using System.Xml;
using TrackViewer.Models;

namespace TrackViewer.Services;

public class GpxExportService
{
    private readonly double _tripThresholdMinutes;

    public GpxExportService(IConfiguration config)
    {
        _tripThresholdMinutes = config.GetValue("Query:TripSegmentThresholdMinutes", 30.0);
    }

    public Task<Stream> ExportAsync(
        ExportRequest           request,
        IReadOnlyList<LocationRecord>  locations,
        IReadOnlyList<WaypointRecord>  waypoints)
    {
        var ms = new MemoryStream();
        var settings = new XmlWriterSettings
        {
            Encoding    = System.Text.Encoding.UTF8,
            Indent      = true,
            Async       = false,
        };

        using var writer = XmlWriter.Create(ms, settings);

        writer.WriteStartDocument();
        writer.WriteStartElement("gpx", "http://www.topografix.com/GPX/1/1");
        writer.WriteAttributeString("version", "1.1");
        writer.WriteAttributeString("creator", request.Creator);
        writer.WriteAttributeString("xmlns", "xsi", null, "http://www.w3.org/2001/XMLSchema-instance");
        writer.WriteAttributeString("xsi", "schemaLocation", "http://www.w3.org/2001/XMLSchema-instance",
            "http://www.topografix.com/GPX/1/1 http://www.topografix.com/GPX/1/1/gpx.xsd");

        // <metadata>
        writer.WriteStartElement("metadata");
        writer.WriteElementString("name", "OwnTracks Export");
        writer.WriteElementString("time", DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ"));
        writer.WriteEndElement();

        // <wpt> per waypoint
        foreach (var wpt in waypoints)
        {
            writer.WriteStartElement("wpt");
            writer.WriteAttributeString("lat", wpt.Latitude.ToString("G10", System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteAttributeString("lon", wpt.Longitude.ToString("G10", System.Globalization.CultureInfo.InvariantCulture));
            writer.WriteElementString("name", wpt.Description ?? $"{wpt.User}/{wpt.Device}");
            writer.WriteElementString("desc", $"{wpt.User}/{wpt.Device}" +
                (wpt.Radius.HasValue ? $" — radius {wpt.Radius} m" : ""));
            writer.WriteElementString("time", DateTimeOffset.FromUnixTimeSeconds(wpt.Timestamp).UtcDateTime
                .ToString("yyyy-MM-ddTHH:mm:ssZ"));
            writer.WriteEndElement();
        }

        // Group by device, segment, write <trk>
        var grouped = locations
            .GroupBy(r => (r.User, r.Device))
            .OrderBy(g => g.Key);

        foreach (var group in grouped)
        {
            writer.WriteStartElement("trk");
            writer.WriteElementString("name", $"{group.Key.User} / {group.Key.Device}");

            var segments = Segment(group.OrderBy(r => r.Timestamp).ToList());
            foreach (var seg in segments)
            {
                writer.WriteStartElement("trkseg");
                foreach (var pt in seg)
                {
                    writer.WriteStartElement("trkpt");
                    writer.WriteAttributeString("lat", pt.Latitude.ToString("G10", System.Globalization.CultureInfo.InvariantCulture));
                    writer.WriteAttributeString("lon", pt.Longitude.ToString("G10", System.Globalization.CultureInfo.InvariantCulture));
                    if (pt.Altitude.HasValue)
                        writer.WriteElementString("ele", pt.Altitude.Value.ToString("G6", System.Globalization.CultureInfo.InvariantCulture));
                    writer.WriteElementString("time",
                        DateTimeOffset.FromUnixTimeSeconds(pt.Timestamp).UtcDateTime
                            .ToString("yyyy-MM-ddTHH:mm:ssZ"));
                    // extensions
                    writer.WriteStartElement("extensions");
                    if (pt.Velocity.HasValue)
                        writer.WriteElementString("speed", pt.Velocity.Value.ToString());
                    if (pt.Battery.HasValue)
                        writer.WriteElementString("battery", pt.Battery.Value.ToString());
                    writer.WriteEndElement(); // extensions
                    writer.WriteEndElement(); // trkpt
                }
                writer.WriteEndElement(); // trkseg
            }

            writer.WriteEndElement(); // trk
        }

        writer.WriteEndElement(); // gpx
        writer.WriteEndDocument();
        writer.Flush();

        ms.Position = 0;
        return Task.FromResult<Stream>(ms);
    }

    public List<List<LocationRecord>> Segment(List<LocationRecord> records)
    {
        var result  = new List<List<LocationRecord>>();
        if (records.Count == 0) return result;

        var current = new List<LocationRecord> { records[0] };
        for (var i = 1; i < records.Count; i++)
        {
            var gapMinutes = (records[i].Timestamp - records[i - 1].Timestamp) / 60.0;
            if (gapMinutes > _tripThresholdMinutes)
            {
                result.Add(current);
                current = new List<LocationRecord>();
            }
            current.Add(records[i]);
        }
        result.Add(current);
        return result;
    }
}
