namespace TrackViewer.Models;

public record DeviceIdentifier(string User, string Device)
{
    public override string ToString() => $"{User}/{Device}";
    public static DeviceIdentifier Parse(string s)
    {
        var idx = s.IndexOf('/');
        return idx < 0
            ? new DeviceIdentifier(s, string.Empty)
            : new DeviceIdentifier(s[..idx], s[(idx + 1)..]);
    }
}
