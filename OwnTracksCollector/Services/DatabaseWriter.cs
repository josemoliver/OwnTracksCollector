using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OwnTracksCollector.Models;
using OwnTracksCollector.Settings;

namespace OwnTracksCollector.Services;

/// <summary>
/// Drains the <see cref="WriteQueue"/> into the database in batches, one transaction each.
/// While the database is unavailable (locked, disk full, ...) the current batch stays in
/// memory and is retried with back-off, so a temporary problem does not lose messages.
/// On shutdown the queue is drained before the service stops.
///
/// Implemented as an <see cref="IHostedService"/> with its own task rather than a
/// BackgroundService, so the drain is guaranteed to run even if shutdown is requested
/// before the loop has had a chance to start.
/// </summary>
public sealed class DatabaseWriter : IHostedService
{
    // Quick extra attempts per batch once shutdown has started, before giving up on it
    private const int ShutdownAttempts = 5;

    // SQLite result codes that usually clear up by themselves
    private static readonly HashSet<int> TransientCodes =
    [
        5,   // SQLITE_BUSY
        6,   // SQLITE_LOCKED
        10,  // SQLITE_IOERR
        13,  // SQLITE_FULL
        14,  // SQLITE_CANTOPEN
    ];

    private readonly WriteQueue _queue;
    private readonly IDatabaseService _db;
    private readonly int _batchSize;
    private readonly ILogger<DatabaseWriter> _logger;
    private readonly CancellationTokenSource _stopping = new();
    private Task? _loop;

    public DatabaseWriter(WriteQueue queue, IDatabaseService db, IOptions<DatabaseOptions> options,
                          ILogger<DatabaseWriter> logger)
    {
        _queue     = queue;
        _db        = db;
        _batchSize = options.Value.BatchSize;
        _logger    = logger;
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _loop = Task.Run(RunAsync, CancellationToken.None);
        return Task.CompletedTask;
    }

    private async Task RunAsync()
    {
        var stoppingToken = _stopping.Token;
        var locations = new List<LocationMessage>();
        var waypoints = new List<WaypointMessage>();

        // After shutdown starts the loop keeps going until the queue has been completed and
        // emptied (see StopAsync); the token only shortens the retry waiting.
        while (await _queue.Reader.WaitToReadAsync(CancellationToken.None))
        {
            locations.Clear();
            waypoints.Clear();

            // Take whatever is already waiting, up to one batch
            while (locations.Count + waypoints.Count < _batchSize && _queue.Reader.TryRead(out var item))
            {
                if (item.Location is not null) locations.Add(item.Location);
                if (item.Waypoint is not null) waypoints.Add(item.Waypoint);
            }

            if (locations.Count + waypoints.Count > 0)
                await WriteWithRetryAsync(locations, waypoints, stoppingToken);
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _queue.Complete();   // the loop finishes once everything queued has been written
        _stopping.Cancel();  // ...and stops waiting between retries

        if (_loop is not null)
        {
            try { await _loop.WaitAsync(cancellationToken); }
            catch (OperationCanceledException)
            {
                _logger.LogError("Shutdown timed out with {Count} row(s) still queued", _queue.Count);
            }
        }
    }

    private async Task WriteWithRetryAsync(List<LocationMessage> locations, List<WaypointMessage> waypoints,
                                           CancellationToken stoppingToken)
    {
        var delay = TimeSpan.FromSeconds(1);
        var shutdownTries = 0;

        while (true)
        {
            try
            {
                var result = _db.Save(locations, waypoints);
                _logger.LogInformation(
                    "Saved {Locations} location(s), {Waypoints} waypoint(s){Dupes} ({Queued} still queued)",
                    result.LocationsInserted, result.WaypointsSaved,
                    result.LocationsSkipped > 0 ? $", skipped {result.LocationsSkipped} duplicate(s)" : "",
                    _queue.Count);
                return;
            }
            catch (Exception ex) when (!IsTransient(ex))
            {
                // Retrying will not help; find the offending row(s) instead of blocking the queue.
                _logger.LogError(ex, "Database rejected a batch of {Count} row(s); saving them one by one",
                    locations.Count + waypoints.Count);
                SaveIndividually(locations, waypoints);
                return;
            }
            catch (Exception ex)
            {
                if (stoppingToken.IsCancellationRequested)
                {
                    if (++shutdownTries > ShutdownAttempts)
                    {
                        _logger.LogError(ex, "Shutting down with {Count} row(s) that could not be written",
                            locations.Count + waypoints.Count);
                        return;
                    }

                    await Task.Delay(500);
                    continue;
                }

                _logger.LogWarning("Database write failed ({Message}); {Count} row(s) kept in memory, retrying in {Delay}s",
                    ex.Message, locations.Count + waypoints.Count, delay.TotalSeconds);

                try { await Task.Delay(delay, stoppingToken); }
                catch (OperationCanceledException) { /* loop once more: logs and gives up */ }

                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
            }
        }
    }

    private void SaveIndividually(List<LocationMessage> locations, List<WaypointMessage> waypoints)
    {
        foreach (var l in locations)
            TrySaveOne([l], []);
        foreach (var w in waypoints)
            TrySaveOne([], [w]);
    }

    private void TrySaveOne(LocationMessage[] locations, WaypointMessage[] waypoints)
    {
        try
        {
            _db.Save(locations, waypoints);
        }
        catch (Exception ex)
        {
            var what = locations.Length > 0
                ? $"location {locations[0].User}/{locations[0].Device} ts={locations[0].Timestamp}"
                : $"waypoint {waypoints[0].User}/{waypoints[0].Device} '{waypoints[0].Description}'";
            _logger.LogError(ex, "Dropped {What}: it could not be stored", what);
        }
    }

    private static bool IsTransient(Exception ex) => ex switch
    {
        SqliteException s => TransientCodes.Contains(s.SqliteErrorCode),
        IOException       => true,
        _                 => false,
    };
}
