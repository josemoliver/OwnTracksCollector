using OwnTracksCollector.Settings;

namespace OwnTracksCollector.Tests;

public class OptionsTests
{
    private readonly MqttOptionsValidator _mqtt = new();
    private readonly DatabaseOptionsValidator _db = new();

    [Fact]
    public void Defaults_are_valid()
    {
        Assert.True(_mqtt.Validate(null, new MqttOptions()).Succeeded);
        Assert.True(_db.Validate(null, new DatabaseOptions()).Succeeded);
    }

    [Fact]
    public void Defaults_favour_not_losing_messages()
    {
        var o = new MqttOptions();
        Assert.Equal(1, o.QualityOfService);
        Assert.False(o.CleanSession);
        Assert.False(string.IsNullOrWhiteSpace(o.ClientId));
    }

    [Fact]
    public void Every_problem_is_reported_at_once()
    {
        var result = _mqtt.Validate(null, new MqttOptions { Host = "", Port = 70000, Topic = " ", ClientId = "", QualityOfService = 3 });

        Assert.True(result.Failed);
        Assert.Equal(5, result.Failures!.Count());
    }

    [Fact]
    public void Database_limits_are_checked()
    {
        var result = _db.Validate(null, new DatabaseOptions { Path = "", BatchSize = 0, QueueCapacity = 0 });
        Assert.Equal(3, result.Failures!.Count());
    }
}
