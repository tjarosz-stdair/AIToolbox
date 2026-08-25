using System.Text.RegularExpressions;
using AIToolbox.Web.Configuration;
using AIToolbox.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace AIToolbox.Web.Tests.Services;

public sealed class SpeechAudioArchiveServiceTests
{
    [Fact]
    public async Task SaveAsync_ValidBasePath_WritesTimestampNamedWavFile()
    {
        var basePath = Path.Combine(Path.GetTempPath(), $"aitoolbox-speech-archive-test-{Guid.NewGuid():N}");
        try
        {
            var service = CreateService(new SpeechAudioStorageOptions { Enabled = true, BasePath = basePath, RetentionDays = 14 });

            await service.SaveAsync([0x52, 0x49, 0x46, 0x46]);

            var written = Directory.GetFiles(basePath, "*.wav", SearchOption.AllDirectories);
            var file = Assert.Single(written);
            Assert.Matches(
                new Regex(@"\d{8}_\d{6}_\d{3}_[0-9a-f]{8}\.wav$"),
                Path.GetFileName(file));
        }
        finally
        {
            if (Directory.Exists(basePath))
            {
                Directory.Delete(basePath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task SaveAsync_BasePathBlockedByAnExistingFile_SwallowsTheFailure()
    {
        // A file sitting where a directory needs to be created makes Directory.CreateDirectory
        // throw IOException — this is the exact "disk/ACL slip" scenario the archive service must
        // survive without ever propagating to its caller (SpeechTranscriptionService).
        var blockingFile = Path.Combine(Path.GetTempPath(), $"aitoolbox-speech-archive-block-{Guid.NewGuid():N}");
        await File.WriteAllTextAsync(blockingFile, "not a directory");
        try
        {
            var basePath = Path.Combine(blockingFile, "speech-audio");
            var service = CreateService(new SpeechAudioStorageOptions { Enabled = true, BasePath = basePath, RetentionDays = 14 });

            var exception = await Record.ExceptionAsync(() => service.SaveAsync([0x52, 0x49, 0x46, 0x46]));

            Assert.Null(exception);
        }
        finally
        {
            File.Delete(blockingFile);
        }
    }

    [Fact]
    public async Task SaveAsync_Disabled_DoesNothing()
    {
        var basePath = Path.Combine(Path.GetTempPath(), $"aitoolbox-speech-archive-disabled-{Guid.NewGuid():N}");
        var service = CreateService(new SpeechAudioStorageOptions { Enabled = false, BasePath = basePath, RetentionDays = 14 });

        await service.SaveAsync([0x52, 0x49, 0x46, 0x46]);

        Assert.False(Directory.Exists(basePath));
    }

    private static SpeechAudioArchiveService CreateService(SpeechAudioStorageOptions options) =>
        new(Options.Create(options), NullLogger<SpeechAudioArchiveService>.Instance);
}
