using Microsoft.Extensions.Logging;
using OwnTracksCollector.Models;
using OwnTracksCollector.Services;

namespace OwnTracksCollector.Tests;

public class OwnTracksParserTests
{
    private static readonly DateTimeOffset Now = new(2026, 6, 1, 12, 0, 0, TimeSpan.Zero);
    private static readonly long Tst = Now.AddMinutes(-5).ToUnixTimeSeconds();
    private const string Topic = "owntracks/jose/phone";

    [Fact]
    public void Location_is_parsed_with_all_fields()
    {
        var json = $$"""
            {"_type":"location","lat":18.4655,"lon":-66.1057,"tst":{{Tst}},"acc":12.5,"alt":30,
             "vel":5,"batt":88,"tid":"jo","t":"u","conn":"w"}
            """;

        var r = OwnTracksParser.Parse(Topic, json, Now);

        var loc = Assert.IsType<LocationMessage>(r.Location);
        Assert.Equal("jose", loc.User);
        Assert.Equal("phone", loc.Device);
        Assert.Equal(18.4655, loc.Latitude);
        Assert.Equal(-66.1057, loc.Longitude);
        Assert.Equal(Tst, loc.Timestamp);
        Assert.Equal(12.5, loc.Accuracy);
        Assert.Equal(88, loc.Battery);
        Assert.Equal("u", loc.Trigger);
        Assert.Equal(json, loc.RawPayload);
        Assert.Empty(r.Issues);
    }

    [Fact]
    public void Missing_tst_uses_arrival_time_and_warns()
    {
        var r = OwnTracksParser.Parse(Topic, """{"_type":"location","lat":18.4,"lon":-66.1}""", Now);

        Assert.Equal(Now.ToUnixTimeSeconds(), r.Location!.Timestamp);
        var issue = Assert.Single(r.Issues);
        Assert.Equal(LogLevel.Warning, issue.Level);
        Assert.Contains("tst", issue.Message);
    }

    [Fact]
    public void Null_and_float_values_are_tolerated()
    {
        var json = $$"""{"_type":"location","lat":18.4,"lon":-66.1,"tst":{{Tst}}.0,"acc":null,"vel":12.6,"batt":null}""";

        var r = OwnTracksParser.Parse(Topic, json, Now);

        Assert.Equal(Tst, r.Location!.Timestamp);
        Assert.Null(r.Location.Accuracy);
        Assert.Equal(13, r.Location.Velocity);
        Assert.Null(r.Location.Battery);
    }

    [Theory]
    [InlineData("""{"_type":"location","lat":91,"lon":0.5,"tst":TST}""")]
    [InlineData("""{"_type":"location","lat":10,"lon":181,"tst":TST}""")]
    [InlineData("""{"_type":"location","lat":0,"lon":0,"tst":TST}""")]
    [InlineData("""{"_type":"location","lat":10,"lon":10,"tst":86400}""")]          // 1970
    [InlineData("""{"_type":"location","lat":10,"lon":10,"tst":4102444800}""")]     // year 2100
    public void Invalid_fixes_are_rejected(string template)
    {
        var r = OwnTracksParser.Parse(Topic, template.Replace("TST", Tst.ToString()), Now);

        Assert.Null(r.Location);
        Assert.Contains(r.Issues, i => i.Level == LogLevel.Warning && i.Message.StartsWith("Rejected"));
    }

    [Fact]
    public void Location_without_coordinates_is_skipped()
    {
        var r = OwnTracksParser.Parse(Topic, """{"_type":"location","tst":1}""", Now);

        Assert.Null(r.Location);
        Assert.Single(r.Issues);
    }

    [Fact]
    public void Single_waypoint_is_parsed()
    {
        var r = OwnTracksParser.Parse(Topic + "/waypoint",
            $$"""{"_type":"waypoint","desc":"Home","lat":18.4,"lon":-66.1,"rad":100,"tst":{{Tst}}}""", Now);

        var wp = Assert.Single(r.Waypoints);
        Assert.Equal("Home", wp.Description);
        Assert.Equal(100, wp.Radius);
        Assert.Equal(Tst, wp.Timestamp);
        Assert.Null(r.Location);
    }

    [Fact]
    public void Bulk_waypoints_keep_the_valid_ones()
    {
        var json = $$"""
            {"_type":"waypoints","waypoints":[
              {"desc":"A","lat":18.4,"lon":-66.1,"tst":{{Tst}}},
              {"desc":"Bad","lat":95,"lon":-66.1,"tst":{{Tst}}},
              {"desc":"C","lat":18.5,"lon":-66.2,"tst":{{Tst}}}]}
            """;

        var r = OwnTracksParser.Parse(Topic + "/waypoints", json, Now);

        Assert.Equal(["A", "C"], r.Waypoints.Select(w => w.Description));
        Assert.Single(r.Issues, i => i.Message.Contains("Bad"));
    }

    [Fact]
    public void Short_topics_are_ignored()
    {
        var r = OwnTracksParser.Parse("owntracks/jose", """{"_type":"location","lat":18.4,"lon":-66.1}""", Now);
        Assert.False(r.HasData);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("[1,2,3]")]
    [InlineData("""{"no_type":true}""")]
    public void Empty_or_untyped_payloads_produce_nothing(string payload)
    {
        var r = OwnTracksParser.Parse(Topic, payload, Now);
        Assert.False(r.HasData);
        Assert.Empty(r.Issues);
    }

    [Fact]
    public void Other_message_types_are_ignored_quietly()
    {
        var r = OwnTracksParser.Parse(Topic, """{"_type":"transition","lat":1,"lon":1}""", Now);

        Assert.False(r.HasData);
        Assert.All(r.Issues, i => Assert.Equal(LogLevel.Debug, i.Level));
    }

    [Fact]
    public void Invalid_json_and_wrong_types_are_reported_not_thrown()
    {
        var bad = OwnTracksParser.Parse(Topic, "{not json", Now);
        var wrong = OwnTracksParser.Parse(Topic, """{"_type":"location","lat":"north","lon":1,"tst":1}""", Now);

        Assert.False(bad.HasData);
        Assert.Equal(LogLevel.Warning, Assert.Single(bad.Issues).Level);
        Assert.False(wrong.HasData);
        Assert.Equal(LogLevel.Warning, Assert.Single(wrong.Issues).Level);
    }
}
