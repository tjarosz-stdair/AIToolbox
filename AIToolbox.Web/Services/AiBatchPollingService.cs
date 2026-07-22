using Serilog.Context;

namespace AIToolbox.Web.Services;

public sealed class AiBatchPollingService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AiBatchPollingService> _logger;

    public AiBatchPollingService(IServiceScopeFactory scopeFactory, ILogger<AiBatchPollingService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(2));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PollOnceAsync(stoppingToken);
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "AI batch polling cycle failed.");
                await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
            }
        }
    }

    private async Task PollOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var repository = scope.ServiceProvider.GetRequiredService<AIToolbox.Web.Data.IAiBatchRepository>();
        var service = scope.ServiceProvider.GetRequiredService<IAiBatchService>();
        var jobs = await repository.GetPollableJobsAsync(cancellationToken);
        foreach (var job in jobs)
        {
            if (job.LastPolledUtc is not null && DateTime.UtcNow - job.LastPolledUtc.Value < TimeSpan.FromMinutes(2))
            {
                continue;
            }

            using var batchScope = LogContext.PushProperty("AiBatchJobId", job.Id);
            await service.PollJobAsync(job.Id, cancellationToken);
        }
    }
}
