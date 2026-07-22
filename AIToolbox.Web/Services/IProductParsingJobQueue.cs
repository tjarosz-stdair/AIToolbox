namespace AIToolbox.Web.Services;

public interface IProductParsingJobQueue
{
    ValueTask QueueAsync(int toolJobId, CancellationToken cancellationToken = default);
    ValueTask<int> DequeueAsync(CancellationToken cancellationToken = default);
    CancellationTokenSource CreateRunningTokenSource(int toolJobId, CancellationToken hostCancellationToken);
    bool RequestCancellation(int toolJobId);
    bool IsCancellationRequested(int toolJobId);
    void Complete(int toolJobId);
}
