using System.Threading.Channels;

namespace AIToolbox.Web.Services.CallEntityExtraction;

/// <summary>Signals "this call's transcript window changed, re-scan it" — the queued item is just
/// a callId, not a payload, since the background service re-reads current window state at dequeue
/// time rather than racing against a stale snapshot.</summary>
public interface ICallEntityExtractionQueue
{
    ValueTask QueueAsync(string callId, CancellationToken cancellationToken = default);

    ValueTask<string> DequeueAsync(CancellationToken cancellationToken = default);
}

public sealed class CallEntityExtractionQueue : ICallEntityExtractionQueue
{
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });

    public ValueTask QueueAsync(string callId, CancellationToken cancellationToken = default)
    {
        return _queue.Writer.WriteAsync(callId, cancellationToken);
    }

    public ValueTask<string> DequeueAsync(CancellationToken cancellationToken = default)
    {
        return _queue.Reader.ReadAsync(cancellationToken);
    }
}
