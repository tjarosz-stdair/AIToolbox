using AIToolbox.Web.Configuration;
using Microsoft.Extensions.Options;

namespace AIToolbox.Web.Services;

public sealed class SpeechAudioArchiveService : ISpeechAudioArchiveService
{
    private readonly SpeechAudioStorageOptions _options;
    private readonly ILogger<SpeechAudioArchiveService> _logger;

    public SpeechAudioArchiveService(IOptions<SpeechAudioStorageOptions> options, ILogger<SpeechAudioArchiveService> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task SaveAsync(byte[] wavBytes, CancellationToken cancellationToken = default)
    {
        if (!_options.Enabled || string.IsNullOrWhiteSpace(_options.BasePath))
        {
            return;
        }

        try
        {
            var basePath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(_options.BasePath.Trim()));
            var now = DateTime.UtcNow;
            var targetDirectory = Path.GetFullPath(Path.Combine(basePath, now.ToString("yyyy"), now.ToString("MM")));

            if (!targetDirectory.StartsWith(basePath, StringComparison.OrdinalIgnoreCase))
            {
                _logger.LogWarning("Configured SpeechAudioStorage:BasePath resolves outside its own root; skipping archive.");
                return;
            }

            Directory.CreateDirectory(targetDirectory);
            // Timestamp-first filename so a listing sorts chronologically and lines up with
            // Serilog's own "yyyy-MM-dd HH:mm:ss.fff" timestamps at a glance; the trailing hex
            // only guards against same-millisecond collisions, not a lookup key back to any
            // request/user.
            var fileName = $"{now:yyyyMMdd_HHmmss_fff}_{Guid.NewGuid().ToString("N")[..8]}.wav";
            var storedPath = Path.Combine(targetDirectory, fileName);
            await File.WriteAllBytesAsync(storedPath, wavBytes, cancellationToken);
        }
        catch (Exception ex)
        {
            // Swallowed deliberately: archiving is a debugging aid, not a hard requirement. A full
            // disk or an ACL slip must never take down live voice input — the caller always
            // proceeds to call Azure regardless of what happens here.
            _logger.LogWarning(ex, "Failed to archive transcoded speech audio to disk.");
        }
    }
}
