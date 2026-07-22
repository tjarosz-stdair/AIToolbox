using Serilog.Context;

namespace AIToolbox.Web.Services;

public sealed class ProductParsingBackgroundService : BackgroundService
{
    private readonly IProductParsingJobQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ProductParsingBackgroundService> _logger;

    public ProductParsingBackgroundService(
        IProductParsingJobQueue queue,
        IServiceScopeFactory scopeFactory,
        ILogger<ProductParsingBackgroundService> logger)
    {
        _queue = queue;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await RequeueInterruptedJobsAsync(stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            int toolJobId;
            try
            {
                toolJobId = await _queue.DequeueAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }

            using var toolScope = LogContext.PushProperty("ToolKey", ProductParsingService.ToolKey);
            using var jobScope = LogContext.PushProperty("ToolJobId", toolJobId);
            using var jobTokenSource = _queue.CreateRunningTokenSource(toolJobId, stoppingToken);
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var parser = scope.ServiceProvider.GetRequiredService<IProductParsingService>();
                await parser.ExecuteQueuedJobAsync(toolJobId, jobTokenSource.Token);
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("Product parse background job {ToolJobId} was cancelled.", toolJobId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Product parse background job {ToolJobId} failed outside the parser service.", toolJobId);
            }
            finally
            {
                _queue.Complete(toolJobId);
            }
        }
    }

    private async Task RequeueInterruptedJobsAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var platform = scope.ServiceProvider.GetRequiredService<AIToolbox.Web.Data.IPlatformRepository>();
            var jobs = await platform.GetActiveToolJobsForToolAsync(ProductParsingService.ToolKey, stoppingToken);
            foreach (var job in jobs)
            {
                await _queue.QueueAsync(job.Id, stoppingToken);
            }

            if (jobs.Count > 0)
            {
                _logger.LogInformation("Requeued {JobCount} interrupted product parse jobs on startup.", jobs.Count);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to requeue interrupted product parse jobs on startup.");
        }
    }
}
