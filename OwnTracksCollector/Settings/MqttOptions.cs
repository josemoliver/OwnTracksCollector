using Microsoft.Extensions.Options;

namespace OwnTracksCollector.Settings;

/// <summary>Strongly typed view of the <c>Mqtt</c> configuration section.</summary>
public sealed class MqttOptions
{
    public const string SectionName = "Mqtt";

    public string  Host     { get; set; } = "localhost";
    public int     Port     { get; set; } = 1883;
    public bool    UseTls   { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string  Topic    { get; set; } = "owntracks/#";

    /// <summary>
    /// Must be stable (and unique per broker) because the default is a persistent session:
    /// the broker identifies the stored session and queued messages by this id.
    /// </summary>
    public string ClientId { get; set; } = "owntracks-collector";

    /// <summary>
    /// False = the broker keeps the session and queues QoS 1/2 messages published while
    /// this collector is offline, delivering them on reconnect. True = start fresh each time.
    /// </summary>
    public bool CleanSession { get; set; }

    /// <summary>Subscription QoS: 0 at most once, 1 at least once (default), 2 exactly once.</summary>
    public int QualityOfService { get; set; } = 1;
}

public sealed class MqttOptionsValidator : IValidateOptions<MqttOptions>
{
    public ValidateOptionsResult Validate(string? name, MqttOptions o)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(o.Host))
            errors.Add("Mqtt:Host must not be empty.");
        if (o.Port is < 1 or > 65535)
            errors.Add($"Mqtt:Port must be between 1 and 65535 (was {o.Port}).");
        if (string.IsNullOrWhiteSpace(o.Topic))
            errors.Add("Mqtt:Topic must not be empty.");
        if (string.IsNullOrWhiteSpace(o.ClientId))
            errors.Add("Mqtt:ClientId must not be empty (it identifies the broker-side session).");
        if (o.QualityOfService is < 0 or > 2)
            errors.Add($"Mqtt:QualityOfService must be 0, 1 or 2 (was {o.QualityOfService}).");

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
