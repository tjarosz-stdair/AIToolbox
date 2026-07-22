using System.Collections.Concurrent;
using System.Threading.Channels;

namespace AIToolbox.Web.Services;

public sealed class ProductParsingJobQueue : IProductParsingJobQueue
{
    private readonly Channel<int> _queue = Channel.CreateUnbounded<int>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false
    });
    private readonly ConcurrentDictionary<int, CancellationTokenSource> _running = new();
    private readonly ConcurrentDictionary<int, byte> _cancelRequested = new();

    public ValueTask QueueAsync(int toolJobId, CancellationToken cancellationToken = default)
    {
        return _queue.Writer.WriteAsync(toolJobId, cancellationToken);
    }

    public ValueTask<int> DequeueAsync(CancellationToken cancellationToken = default)
    {
        return _queue.Reader.ReadAsync(cancellationToken);
    }

    public CancellationTokenSource CreateRunningTokenSource(int toolJobId, CancellationToken hostCancellationToken)
    {
        var source = CancellationTokenSource.CreateLinkedTokenSource(hostCancellationToken);
        if (_cancelRequested.ContainsKey(toolJobId))
        {
            source.Cancel();
        }

        _running[toolJobId] = source;
        return source;
    }

    public bool RequestCancellation(int toolJobId)
    {
        _cancelRequested[toolJobId] = 1;
        if (_running.TryGetValue(toolJobId, out var source))
        {
            source.Cancel();
            return true;
        }

        return false;
    }

    public bool IsCancellationRequested(int toolJobId)
    {
        return _cancelRequested.ContainsKey(toolJobId);
    }

    public void Complete(int toolJobId)
    {
        _running.TryRemove(toolJobId, out var source);
        source?.Dispose();
        _cancelRequested.TryRemove(toolJobId, out _);
    }
}
