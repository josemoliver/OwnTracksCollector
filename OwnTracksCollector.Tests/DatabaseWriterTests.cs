using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using OwnTracksCollector.Models;
using OwnTracksCollector.Services;
using OwnTracksCollector.Settings;
using MsOptions = Microsoft.Extensions.Options.Options;

namespace OwnTracksCollector.Tests;

public class DatabaseWriterTests
{
    private static Microsoft.Extensions.Options.IOptions<DatabaseOptions> Opts(int batch = 500) =>
        MsOptions.Create(new DatabaseOptions { BatchSize = batch, QueueCapacity = 1000 });

    private sealed class FakeDb : IDatabaseService
    {
        public readonly List<(int Locations, int Waypoints)> Batches = [];
        public Func<IReadOnlyList<LocationMessage>, IReadOnlyList<WaypointMessage>, SaveResult>? Behaviour;

        public void Initialize() { }

        public SaveResult Save(IReadOnlyList<LocationMessage> l, IReadOnlyList<WaypointMessage> w)
        {
            var r = Behaviour?.Invoke(l, w) ?? new SaveResult(l.Count, 0, w.Count);
            Batches.Add((l.Count, w.Count));   // only reached when Behaviour did not throw
            return r;
        }
    }

    private static PendingWrite L(long t) =>
        new(new LocationMessage { User = "u", Device = "d", Latitude = 1, Longitude = 1, Timestamp = t }, null);

    private static async Task<DatabaseWriter> StartAsync(WriteQueue q, FakeDb db, int batch = 500)
    {
        var w = new DatabaseWriter(q, db, Opts(batch), NullLogger<DatabaseWriter>.Instance);
        await w.StartAsync(CancellationToken.None);
        return w;
    }

    [Fact]
    public async Task Queued_rows_are_written_in_batches_and_flushed_on_stop()
    {
        var q = new WriteQueue(Opts());
        var db = new FakeDb();
        for (var i = 0; i < 25; i++) await q.EnqueueAsync(L(i));

        var writer = await StartAsync(q, db, batch: 10);
        await writer.StopAsync(CancellationToken.None);

        Assert.Equal(25, db.Batches.Sum(b => b.Locations));
        Assert.All(db.Batches, b => Assert.InRange(b.Locations, 1, 10));
    }

    [Fact]
    public async Task Transient_failure_is_retried_without_losing_rows()
    {
        var q = new WriteQueue(Opts());
        var attempts = 0;
        var db = new FakeDb
        {
            Behaviour = (l, w) => ++attempts < 3
                ? throw new SqliteException("database is locked", 5)
                : new SaveResult(l.Count, 0, w.Count)
        };
        await q.EnqueueAsync(L(1));
        await q.EnqueueAsync(L(2));

        var writer = await StartAsync(q, db);
        await Task.Delay(100);                                   // first attempt fails, back-off begins
        await writer.StopAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));

        // Shutdown skips the long back-off and retries quickly, so the rows still get written
        Assert.Equal(3, attempts);
        Assert.Equal(2, db.Batches.Single().Locations);
    }

    [Fact]
    public async Task Rejected_row_does_not_block_the_others()
    {
        var q = new WriteQueue(Opts());
        var db = new FakeDb
        {
            Behaviour = (l, w) => l.Any(x => x.Timestamp == 2)
                ? throw new SqliteException("constraint failed", 19)
                : new SaveResult(l.Count, 0, w.Count)
        };
        foreach (var t in new long[] { 1, 2, 3 }) await q.EnqueueAsync(L(t));

        var writer = await StartAsync(q, db);
        await writer.StopAsync(CancellationToken.None);

        Assert.Equal(2, db.Batches.Count(b => b.Locations == 1));   // rows 1 and 3 stored individually
    }
}
