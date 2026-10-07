using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OwnTracksCollector.Services;

namespace OwnTracksCollector;

/// <summary>
/// Long-running hosted service. Manages the MQTT connection for the lifetime
/// of the application, whether run interactively or as a Windows Service.
/// </summary>
public class Worker : BackgroundService
{
    private readonly MqttService _mqttService;
    private readonly IDatabaseService _dbService;
    private readonly ILogger<Worker> _logger;
    private readonly IHostApplicationLifetime _lifetime;

    public Worker(
        MqttService mqttService,
        IDatabaseService dbService,
        ILogger<Worker> logger,
        IHostApplicationLifetime lifetime)
    {
        _mqttService = mqttService;
        _dbService   = dbService;
        _logger      = logger;
        _lifetime    = lifetime;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("=== OwnTracks Location Collector starting ===");

        _dbService.Initialize();

        try
        {
            await _mqttService.ConnectAndSubscribeAsync(stoppingToken);
        }
        catch (Exception ex)
        {
            _logger.LogCritical(ex, "MQTT service failed to start. Stopping.");
            _lifetime.StopApplication();
            return;
        }

        _logger.LogInformation("Listening for location updates. Press Ctrl+C to stop.");

        // Keep the worker alive until the host requests shutdown
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown
        }

        await _mqttService.DisconnectAsync();
        _logger.LogInformation("OwnTracks Location Collector stopped.");
    }
}
