using System.Threading.Channels;
using Microsoft.Extensions.Options;
using OwnTracksCollector.Models;
using OwnTracksCollector.Settings;

namespace OwnTracksCollector.Services;

/// <summary>One row waiting to be written: either a location or a waypoint.</summary>
public readonly record struct PendingWrite(LocationMessage? Location, WaypointMessage? Waypoint);

/// <summary>
/// In-memory hand-off between the MQTT receive loop and <see cref="DatabaseWriter"/>.
/// Bounded: when the writer falls far behind, <see cref="EnqueueAsync"/> waits rather than
/// dropping data or growing without limit.
/// </summary>
public sealed class WriteQueue
{
    private readonly Channel<PendingWrite> _channel;

    public WriteQueue(IOptions<DatabaseOptions> options)
    {
        _channel = Channel.CreateBounded<PendingWrite>(new BoundedChannelOptions(options.Value.QueueCapacity)
        {
            FullMode     = BoundedChannelFullMode.Wait,
            SingleReader = true,
        });
    }

    public ChannelReader<PendingWrite> Reader => _channel.Reader;

    /// <summary>Rows currently waiting.</summary>
    public int Count => _channel.Reader.Count;

    public ValueTask EnqueueAsync(PendingWrite item, CancellationToken ct = default) =>
        _channel.Writer.WriteAsync(item, ct);

    /// <summary>No more rows will be added; the reader finishes once the queue is empty.</summary>
    public void Complete() => _channel.Writer.TryComplete();
}
