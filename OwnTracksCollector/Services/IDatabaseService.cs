using OwnTracksCollector.Models;

namespace OwnTracksCollector.Services;

/// <summary>Outcome of one <see cref="IDatabaseService.Save"/> call.</summary>
public readonly record struct SaveResult(int LocationsInserted, int LocationsSkipped, int WaypointsSaved);

/// <summary>Persistence boundary for the collector, so the pipeline can be tested without SQLite.</summary>
public interface IDatabaseService
{
    /// <summary>Creates the database, schema and indexes if needed. Safe to call on every start.</summary>
    void Initialize();

    /// <summary>
    /// Writes the given rows in a single transaction: all of them are stored or none are.
    /// Duplicate locations are skipped; a re-sent waypoint replaces the stored one.
    /// </summary>
    SaveResult Save(IReadOnlyList<LocationMessage> locations, IReadOnlyList<WaypointMessage> waypoints);
}
