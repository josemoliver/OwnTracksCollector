using System.Text.Json;
using Microsoft.Extensions.Logging;
using OwnTracksCollector.Models;

namespace OwnTracksCollector.Services;

/// <summary>Something worth logging that happened while parsing a message.</summary>
public sealed record ParseIssue(LogLevel Level, string Message);

/// <summary>What one MQTT message turned into: at most one location, any number of waypoints.</summary>
public sealed record ParseResult(
    LocationMessage?               Location,
    IReadOnlyList<WaypointMessage> Waypoints,
    IReadOnlyList<ParseIssue>      Issues)
{
    public static readonly ParseResult None = new(null, [], []);

    public bool HasData => Location is not null || Waypoints.Count > 0;
}

/// <summary>
/// Turns an OwnTracks MQTT message (topic + JSON payload) into storable records.
/// Pure: no I/O, no logging, no clock except the optional <c>now</c> argument, so it can be
/// unit-tested without a broker or database.
///
/// Topic format: owntracks/{user}/{device}[/sub-topic]
/// Handled types: location, waypoint, waypoints (bulk). Everything else is ignored.
/// </summary>
public static class OwnTracksParser
{
    public static ParseResult Parse(string topic, string? payload, DateTimeOffset? now = null)
    {
        // Empty payloads are how retained messages get cleared
        if (string.IsNullOrWhiteSpace(payload))
            return ParseResult.None;

        var parts = topic.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3)
            return ParseResult.None;

        var user   = parts[1];
        var device = parts[2];
        var clock  = (now ?? DateTimeOffset.UtcNow).ToUnixTimeSeconds();
        var issues = new List<ParseIssue>();

        try
        {
            using var doc = JsonDocument.Parse(payload);
            var root = doc.RootElement;

            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("_type", out var typeElement))
                return ParseResult.None;

            switch (typeElement.GetString())
            {
                case "location":
                    return ParseLocation(root, user, device, payload, clock, now, issues);

                case "waypoint":
                {
                    var wps = new List<WaypointMessage>();
                    AddWaypoint(root, user, device, payload, clock, now, wps, issues);
                    return new ParseResult(null, wps, issues);
                }

                case "waypoints":
                {
                    var wps = new List<WaypointMessage>();
                    if (root.TryGetProperty("waypoints", out var list) && list.ValueKind == JsonValueKind.Array)
                        foreach (var item in list.EnumerateArray())
                            AddWaypoint(item, user, device, item.GetRawText(), clock, now, wps, issues);
                    return new ParseResult(null, wps, issues);
                }

                default:
                    issues.Add(new(LogLevel.Debug, $"Ignored message type: {typeElement.GetString()}"));
                    return new ParseResult(null, [], issues);
            }
        }
        catch (JsonException ex)
        {
            issues.Add(new(LogLevel.Warning, $"Could not parse JSON: {ex.Message}"));
        }
        catch (Exception ex) when (ex is InvalidOperationException or FormatException or OverflowException)
        {
            issues.Add(new(LogLevel.Warning, $"Malformed {topic} message: {ex.Message}"));
        }

        return new ParseResult(null, [], issues);
    }

    private static ParseResult ParseLocation(JsonElement root, string user, string device, string raw,
                                             long clock, DateTimeOffset? now, List<ParseIssue> issues)
    {
        if (!root.TryGetProperty("lat", out var lat) || !root.TryGetProperty("lon", out var lon))
        {
            issues.Add(new(LogLevel.Warning, $"Location from {user}/{device} has no lat/lon"));
            return new ParseResult(null, [], issues);
        }

        // OwnTracks normally sends tst; when absent the arrival time is used, which is wrong
        // for delayed or queued messages, so flag it.
        var tst = OptLong(root, "tst");
        if (tst is null)
            issues.Add(new(LogLevel.Warning, $"Location from {user}/{device} has no 'tst'; using arrival time"));

        var msg = new LocationMessage
        {
            User       = user,
            Device     = device,
            Latitude   = lat.GetDouble(),
            Longitude  = lon.GetDouble(),
            Timestamp  = tst ?? clock,
            Accuracy   = OptDouble(root, "acc"),
            Altitude   = OptDouble(root, "alt"),
            Velocity   = OptInt(root, "vel"),
            Battery    = OptInt(root, "batt"),
            TrackerId  = OptString(root, "tid"),
            Trigger    = OptString(root, "t"),
            Connection = OptString(root, "conn"),
            RawPayload = raw,
        };

        var problem = FixValidator.Validate(msg.Latitude, msg.Longitude, msg.Timestamp, now);
        if (problem is not null)
        {
            issues.Add(new(LogLevel.Warning, $"Rejected location from {user}/{device}: {problem}"));
            return new ParseResult(null, [], issues);
        }

        return new ParseResult(msg, [], issues);
    }

    private static void AddWaypoint(JsonElement el, string user, string device, string raw, long clock,
                                    DateTimeOffset? now, List<WaypointMessage> into, List<ParseIssue> issues)
    {
        if (el.ValueKind != JsonValueKind.Object ||
            !el.TryGetProperty("lat", out var lat) || !el.TryGetProperty("lon", out var lon))
        {
            issues.Add(new(LogLevel.Warning, $"Waypoint from {user}/{device} has no lat/lon"));
            return;
        }

        var wp = new WaypointMessage
        {
            User        = user,
            Device      = device,
            Description = OptString(el, "desc"),
            Latitude    = lat.GetDouble(),
            Longitude   = lon.GetDouble(),
            Radius      = OptInt(el, "rad"),
            Timestamp   = OptLong(el, "tst") ?? clock,
            RawPayload  = raw,
        };

        var problem = FixValidator.Validate(wp.Latitude, wp.Longitude, wp.Timestamp, now);
        if (problem is not null)
        {
            issues.Add(new(LogLevel.Warning, $"Rejected waypoint '{wp.Description}' from {user}/{device}: {problem}"));
            return;
        }

        into.Add(wp);
    }

    // ── Tolerant readers: absent or null -> null; whole numbers sent as floats are accepted ──

    private static JsonElement? Get(JsonElement el, string name) =>
        el.TryGetProperty(name, out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            ? v : null;

    private static double? OptDouble(JsonElement el, string name) => Get(el, name)?.GetDouble();

    private static long? OptLong(JsonElement el, string name) =>
        Get(el, name) is { } v ? (v.TryGetInt64(out var l) ? l : (long)Math.Round(v.GetDouble())) : null;

    private static int? OptInt(JsonElement el, string name) =>
        Get(el, name) is { } v ? (v.TryGetInt32(out var i) ? i : (int)Math.Round(v.GetDouble())) : null;

    private static string? OptString(JsonElement el, string name) =>
        Get(el, name) is { ValueKind: JsonValueKind.String } v ? v.GetString() : null;
}
