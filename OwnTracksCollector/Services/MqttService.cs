using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using MQTTnet;
using MQTTnet.Protocol;
using OwnTracksCollector.Settings;

namespace OwnTracksCollector.Services;

/// <summary>
/// Manages the MQTT lifecycle: connecting to the broker (retrying until it succeeds),
/// subscribing to the configured topic, handing inbound messages to <see cref="OwnTracksParser"/>
/// and queueing the results for <see cref="DatabaseWriter"/>, and reconnecting automatically
/// when the connection drops.
///
/// Delivery: the subscription uses QoS 1 and, by default, a persistent session with a stable
/// client id, so messages published while the collector is offline are queued by the broker
/// and delivered on reconnect. That can mean duplicates, which the database ignores.
/// </summary>
public class MqttService
{
    private static readonly TimeSpan MaxRetryDelay = TimeSpan.FromSeconds(60);

    private readonly MqttOptions _options;
    private readonly WriteQueue _queue;
    private readonly ILogger<MqttService> _logger;

    private IMqttClient? _client;
    private MqttClientOptions? _clientOptions;

    // Cancelled by DisconnectAsync (or the host token) to stop connecting/reconnecting.
    private CancellationTokenSource _lifetime = new();

    // 1 while a connect loop is running, so a Disconnected event raised by a failed attempt
    // does not start a second loop.
    private int _connecting;

    public MqttService(IOptions<MqttOptions> options, WriteQueue queue, ILogger<MqttService> logger)
    {
        _options = options.Value;
        _queue   = queue;
        _logger  = logger;
    }

    /// <summary>
    /// Builds the client, then connects and subscribes. If the broker is not reachable it keeps
    /// retrying with exponential back-off (2 s → 60 s) until it connects or <paramref name="ct"/>
    /// is cancelled, so starting before the broker (e.g. at boot) is fine.
    /// </summary>
    public async Task ConnectAndSubscribeAsync(CancellationToken ct = default)
    {
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);

        var factory = new MqttClientFactory();
        _client = factory.CreateMqttClient();

        var builder = new MqttClientOptionsBuilder()
            .WithTcpServer(_options.Host, _options.Port)
            .WithClientId(_options.ClientId)
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(60))
            .WithCleanSession(_options.CleanSession);

        // Only attach credentials when a username is provided; anonymous brokers omit them.
        if (!string.IsNullOrWhiteSpace(_options.Username))
            builder.WithCredentials(_options.Username, _options.Password);

        // TLS is required for cloud brokers (HiveMQ Cloud uses port 8883).
        if (_options.UseTls)
            builder.WithTlsOptions(o => o.UseTls());

        _clientOptions = builder.Build();

        _client.ApplicationMessageReceivedAsync += OnMessageReceivedAsync;
        _client.DisconnectedAsync               += OnDisconnectedAsync;

        await ConnectWithRetryAsync(_lifetime.Token);
    }

    /// <summary>
    /// Connects and (re)subscribes, retrying until it works or shutdown is requested.
    /// Used for the first connection and for every reconnect.
    /// </summary>
    private async Task ConnectWithRetryAsync(CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _connecting, 1, 0) != 0)
            return;   // another loop is already on it

        try
        {
            var delay = TimeSpan.FromSeconds(2);
            while (!ct.IsCancellationRequested && _client is not null && _clientOptions is not null)
            {
                try
                {
                    if (!_client.IsConnected)
                        await _client.ConnectAsync(_clientOptions, ct);
                    _logger.LogInformation("Connected to {Host}:{Port} (TLS={UseTls}, clean session={Clean})",
                        _options.Host, _options.Port, _options.UseTls, _options.CleanSession);

                    await SubscribeAsync(ct);
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning("Could not connect to {Host}:{Port}: {Message}. Retrying in {Delay}s…",
                        _options.Host, _options.Port, ex.Message, delay.TotalSeconds);

                    try { await Task.Delay(delay, ct); }
                    catch (OperationCanceledException) { return; }

                    delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, MaxRetryDelay.TotalSeconds));
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _connecting, 0);
        }
    }

    /// <summary>
    /// Sends a SUBSCRIBE for the configured topic at the configured QoS. Re-issued after every
    /// reconnect: harmless when the broker kept the session, required when it did not.
    /// </summary>
    private async Task SubscribeAsync(CancellationToken ct)
    {
        if (_client is null) return;

        var qos = (MqttQualityOfServiceLevel)_options.QualityOfService;
        var subscribeOptions = new MqttClientFactory().CreateSubscribeOptionsBuilder()
            .WithTopicFilter(f => f.WithTopic(_options.Topic).WithQualityOfServiceLevel(qos))
            .Build();

        await _client.SubscribeAsync(subscribeOptions, ct);
        _logger.LogInformation("Subscribed to topic: {Topic} (QoS {Qos})", _options.Topic, (int)qos);
    }

    /// <summary>Fired whenever the connection to the broker is lost; reconnects with back-off.</summary>
    private async Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs e)
    {
        if (_lifetime.IsCancellationRequested) return;

        if (e.ClientWasConnected)
            _logger.LogWarning("Disconnected: {Reason}. Reconnecting…", e.Reason);

        await ConnectWithRetryAsync(_lifetime.Token);
    }

    /// <summary>
    /// Invoked by MQTTnet for every inbound PUBLISH. Parsing is pure and cheap; the database write
    /// happens later on the writer's thread, so a slow disk never stalls the receive loop
    /// (until the bounded queue is full).
    /// </summary>
    private async Task OnMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs e)
    {
        var topic   = e.ApplicationMessage.Topic;
        var payload = e.ApplicationMessage.ConvertPayloadToString() ?? string.Empty;

        _logger.LogDebug("{Topic} → {Payload}", topic, payload);

        try
        {
            var result = OwnTracksParser.Parse(topic, payload);

            foreach (var issue in result.Issues)
                _logger.Log(issue.Level, "{Topic}: {Message}", topic, issue.Message);

            if (result.Location is not null)
                await _queue.EnqueueAsync(new PendingWrite(result.Location, null), _lifetime.Token);

            foreach (var wp in result.Waypoints)
                await _queue.EnqueueAsync(new PendingWrite(null, wp), _lifetime.Token);
        }
        catch (OperationCanceledException) { /* shutting down */ }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing message on {Topic}", topic);
        }
    }

    /// <summary>
    /// Performs a clean MQTT disconnect. Stops any reconnect loop first so that a deliberate
    /// shutdown does not trigger an unwanted reconnect. Called by <see cref="Worker"/> on shutdown.
    /// </summary>
    public async Task DisconnectAsync()
    {
        _lifetime.Cancel();

        if (_client?.IsConnected == true)
        {
            _client.DisconnectedAsync -= OnDisconnectedAsync;
            await _client.DisconnectAsync();
            _logger.LogInformation("Disconnected gracefully.");
        }
    }
}
