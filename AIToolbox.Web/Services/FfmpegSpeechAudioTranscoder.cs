using System.Diagnostics;
using AIToolbox.Web.Configuration;
using Microsoft.Extensions.Options;

namespace AIToolbox.Web.Services;

public sealed class FfmpegSpeechAudioTranscoder : ISpeechAudioTranscoder
{
    private readonly FfmpegOptions _options;
    private readonly ILogger<FfmpegSpeechAudioTranscoder> _logger;

    public FfmpegSpeechAudioTranscoder(IOptions<FfmpegOptions> options, ILogger<FfmpegSpeechAudioTranscoder> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public async Task<SpeechTranscodeResult> TranscodeToWavAsync(Stream input, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_options.ExecutablePath))
        {
            _logger.LogError("Speech audio transcoding was requested but Ffmpeg:ExecutablePath is not configured.");
            return SpeechTranscodeResult.Failure("Voice input isn't fully configured yet.");
        }

        var startInfo = new ProcessStartInfo
        {
            FileName = _options.ExecutablePath,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        // ffmpeg format-probes the input byte stream itself, so no -f flag or filename hint is
        // needed on the input side even though the client's Content-Type varies by browser
        // (audio/webm;codecs=opus in Chrome, audio/ogg;codecs=opus in Firefox).
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add("pipe:0");
        startInfo.ArgumentList.Add("-vn");
        startInfo.ArgumentList.Add("-ac");
        startInfo.ArgumentList.Add("1");
        startInfo.ArgumentList.Add("-ar");
        startInfo.ArgumentList.Add("16000");
        startInfo.ArgumentList.Add("-c:a");
        startInfo.ArgumentList.Add("pcm_s16le");
        startInfo.ArgumentList.Add("-f");
        startInfo.ArgumentList.Add("wav");
        startInfo.ArgumentList.Add("pipe:1");

        using var process = new Process { StartInfo = startInfo };
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(1, _options.TimeoutSeconds)));

        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start ffmpeg process at {ExecutablePath}.", _options.ExecutablePath);
            return SpeechTranscodeResult.Failure("Voice input isn't fully configured yet.");
        }

        // Stdin write, stdout read, and stderr read must run concurrently, not sequentially:
        // writing all of stdin before reading stdout would deadlock once the OS pipe buffer fills,
        // since ffmpeg blocks writing output while nothing is draining it.
        var stdoutTask = ReadAllBytesAsync(process.StandardOutput.BaseStream, timeoutCts.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeoutCts.Token);
        var stdinTask = WriteAndCloseAsync(process, input, timeoutCts.Token);

        try
        {
            await Task.WhenAll(stdinTask, stdoutTask, stderrTask);
            await process.WaitForExitAsync(timeoutCts.Token);
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            _logger.LogWarning("ffmpeg transcoding timed out after {TimeoutSeconds}s.", _options.TimeoutSeconds);
            return SpeechTranscodeResult.Failure("Converting that recording took too long.");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw; // caller's own cancellation (e.g. client disconnect) — propagate, don't mask
        }

        var stderrExcerpt = CreateExcerpt(await stderrTask);

        if (process.ExitCode != 0)
        {
            // The "ffmpeg failed to convert audio" failure mode — distinguishable in logs from an
            // Azure-side failure by construction: it never reaches the Azure HTTP call.
            _logger.LogWarning(
                "ffmpeg exited with code {ExitCode} while transcoding speech audio: {StdErr}",
                process.ExitCode, stderrExcerpt);
            return SpeechTranscodeResult.Failure("Couldn't process that recording.", stderrExcerpt);
        }

        var wavBytes = await stdoutTask;
        if (wavBytes.Length == 0)
        {
            _logger.LogWarning("ffmpeg exited 0 but produced no output while transcoding speech audio: {StdErr}", stderrExcerpt);
            return SpeechTranscodeResult.Failure("Couldn't process that recording.", stderrExcerpt);
        }

        return SpeechTranscodeResult.Success(wavBytes);
    }

    private static async Task WriteAndCloseAsync(Process process, Stream input, CancellationToken cancellationToken)
    {
        try
        {
            await input.CopyToAsync(process.StandardInput.BaseStream, cancellationToken);
        }
        finally
        {
            process.StandardInput.Close(); // signals EOF to ffmpeg's stdin reader
        }
    }

    private static async Task<byte[]> ReadAllBytesAsync(Stream stdout, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        await stdout.CopyToAsync(buffer, cancellationToken);
        return buffer.ToArray();
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
            // best-effort; process may have already exited between the check and the kill
        }
    }

    private static string CreateExcerpt(string value)
    {
        const int maxLength = 500;
        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
