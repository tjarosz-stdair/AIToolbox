using AIToolbox.Web.Configuration;
using Microsoft.Extensions.Options;

namespace AIToolbox.Web.Services;

/// <summary>
/// Daily purge of transcoded WAV files older than SpeechAudioStorage:RetentionDays. Mirrors
/// ConversationRetentionPurgeService's PeriodicTimer/try-catch shape. Unlike that service, this
/// job has no scoped repository dependency (it's pure filesystem work against an Options-bound
/// path), so it resolves SpeechAudioStorageOptions directly via IOptions rather than creating a
/// DI scope each tick — IOptions (not IOptionsMonitor) is intentional: per the "redeploy to
/// change" contract this setting shares with AIToolsLoggingOptions, a changed RetentionDays value
/// only takes effect after the app restarts, same as every other Options-bound setting here.
/// </summary>
public sealed class SpeechAudioRetentionPurgeService : BackgroundService
{
    private readonly SpeechAudioStorageOptions _options;
    private readonly ILogger<SpeechAudioRetentionPurgeService> _logger;

    public SpeechAudioRetentionPurgeService(
        IOptions<SpeechAudioStorageOptions> options,
        ILogger<SpeechAudioRetentionPurgeService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromHours(24));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                PurgeOnce();
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Speech audio retention purge cycle failed.");
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

    private void PurgeOnce()
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.BasePath))
        {
            return;
        }

        var basePath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(_options.BasePath.Trim()));
        if (!Directory.Exists(basePath))
        {
            return;
        }

        var cutoffUtc = DateTime.UtcNow - TimeSpan.FromDays(Math.Max(0, _options.RetentionDays));
        var deletedCount = 0;

        foreach (var filePath in Directory.EnumerateFiles(basePath, "*.wav", SearchOption.AllDirectories))
        {
            try
            {
                if (File.GetLastWriteTimeUtc(filePath) < cutoffUtc)
                {
                    File.Delete(filePath);
                    deletedCount++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to delete expired speech audio file {FilePath}.", filePath);
            }
        }

        if (deletedCount > 0)
        {
            _logger.LogInformation(
                "Speech audio retention purge deleted {Count} files older than {RetentionDays} days.",
                deletedCount, _options.RetentionDays);
        }
    }
}
