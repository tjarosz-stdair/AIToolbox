using AIToolbox.Web.Data;

namespace AIToolbox.Web.Services;

/// <summary>
/// Daily purge of closed conversations past the tenant-configured retention window (Part 4.5.3).
/// Only deletes ConversationMessages/Conversations rows — the ConversationUsageRollup stats these
/// conversations already contributed were written in real time as events happened (Part 4.5.8),
/// not reconstructed here, so purging never risks losing statistics.
/// </summary>
public sealed class ConversationRetentionPurgeService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ConversationRetentionPurgeService> _logger;

    public ConversationRetentionPurgeService(
        IServiceScopeFactory scopeFactory,
        ILogger<ConversationRetentionPurgeService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await PurgeOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Conversation retention purge cycle failed.");
            }

            try
            {
                await timer.WaitForNextTickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task PurgeOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var retentionSettings = scope.ServiceProvider.GetRequiredService<IRetentionSettingsRepository>();
        var conversations = scope.ServiceProvider.GetRequiredService<IConversationRepository>();

        var settings = await retentionSettings.GetAsync(cancellationToken);
        var cutoffUtc = DateTime.UtcNow - TimeSpan.FromDays(settings.ConversationRetentionDays);

        var purgeableIds = await conversations.GetPurgeableConversationIdsAsync(cutoffUtc, cancellationToken);
        foreach (var conversationId in purgeableIds)
        {
            await conversations.DeleteConversationAsync(conversationId, cancellationToken);
        }

        if (purgeableIds.Count > 0)
        {
            _logger.LogInformation(
                "Conversation retention purge deleted {Count} conversations older than {RetentionDays} days.",
                purgeableIds.Count,
                settings.ConversationRetentionDays);
        }
    }
}
