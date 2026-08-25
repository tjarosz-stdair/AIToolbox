using System.Net.Http.Headers;
using System.Text.Json;
using AIToolbox.Web.Models;
using AIToolbox.Web.Security;

namespace AIToolbox.Web.Services;

public sealed class SpeechTranscriptionService : ISpeechTranscriptionService
{
    private const string DefaultRegion = "eastus";
    private const string DefaultLanguage = "en-US";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ISecretProtector _secretProtector;
    private readonly ILogger<SpeechTranscriptionService> _logger;

    public SpeechTranscriptionService(
        IHttpClientFactory httpClientFactory,
        ISecretProtector secretProtector,
        ILogger<SpeechTranscriptionService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _secretProtector = secretProtector;
        _logger = logger;
    }

    public async Task<SpeechTranscriptionResult> TranscribeAsync(
        ExternalResource resource,
        Stream audio,
        string? contentType,
        CancellationToken cancellationToken = default)
    {
        var (region, language) = ParseConfig(resource.ConfigJson);
        var key = _secretProtector.Unprotect(resource.SecretCipherText);
        if (string.IsNullOrWhiteSpace(key))
        {
            return SpeechTranscriptionResult.Failure("No Azure Speech subscription key is configured.");
        }

        // format=detailed adds NBest (confidence scores, per-alternative text) and Duration/Offset
        // to the response -- purely diagnostic value (never surfaced to the browser), but the
        // difference between "Duration present and reasonable" and "Duration 0/absent" is exactly
        // what separates "Azure genuinely couldn't recognize this audio" from "the audio never
        // reached Azure intact" when a turn comes back as NoMatch or Success-with-no-text.
        var endpoint = $"https://{region}.stt.speech.microsoft.com/speech/recognition/conversation/cognitiveservices/v1?language={Uri.EscapeDataString(language)}&format=detailed";

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
            request.Headers.TryAddWithoutValidation("Ocp-Apim-Subscription-Key", key);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Content = new StreamContent(audio);
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(
                string.IsNullOrWhiteSpace(contentType) ? "audio/webm; codecs=opus" : contentType);

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));

            var client = _httpClientFactory.CreateClient("AzureSpeech");
            using var response = await client.SendAsync(request, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Azure Speech transcription failed with {StatusCode}: {Body}", (int)response.StatusCode, CreateExcerpt(body));
                return SpeechTranscriptionResult.Failure($"Azure Speech returned {(int)response.StatusCode}.");
            }

            // Logged unconditionally (success AND NoMatch/empty-text alike) -- this exact path was
            // previously silent on anything but a bad HTTP status or a thrown exception, which is
            // why a live "Success but no text" turn left no trace anywhere to diagnose from.
            _logger.LogInformation("Azure Speech response (HTTP 200): {Body}", CreateExcerpt(body));

            return ParseRecognitionResponse(body);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return SpeechTranscriptionResult.Failure("The speech recognition request timed out.");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Azure Speech transcription threw an exception.");
            return SpeechTranscriptionResult.Failure("The speech recognition request failed.");
        }
    }

    private static SpeechTranscriptionResult ParseRecognitionResponse(string body)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            var status = root.TryGetProperty("RecognitionStatus", out var statusEl) ? statusEl.GetString() : null;

            if (string.Equals(status, "Success", StringComparison.OrdinalIgnoreCase))
            {
                var text = root.TryGetProperty("DisplayText", out var textEl) ? textEl.GetString() : null;
                return string.IsNullOrWhiteSpace(text)
                    ? SpeechTranscriptionResult.Failure("Didn't catch that — no speech was recognized.")
                    : SpeechTranscriptionResult.Success(text);
            }

            return SpeechTranscriptionResult.Failure(
                string.Equals(status, "NoMatch", StringComparison.OrdinalIgnoreCase)
                    ? "Didn't catch that — try again."
                    : $"Speech recognition status: {status ?? "unknown"}.");
        }
        catch (JsonException)
        {
            return SpeechTranscriptionResult.Failure("Azure Speech returned an unexpected response.");
        }
    }

    private static (string Region, string Language) ParseConfig(string? configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
        {
            return (DefaultRegion, DefaultLanguage);
        }

        try
        {
            using var document = JsonDocument.Parse(configJson);
            var region = document.RootElement.TryGetProperty("region", out var regionEl) ? regionEl.GetString() : null;
            var language = document.RootElement.TryGetProperty("language", out var languageEl) ? languageEl.GetString() : null;
            return (
                string.IsNullOrWhiteSpace(region) ? DefaultRegion : region,
                string.IsNullOrWhiteSpace(language) ? DefaultLanguage : language);
        }
        catch (JsonException)
        {
            return (DefaultRegion, DefaultLanguage);
        }
    }

    private static string CreateExcerpt(string value)
    {
        const int maxLength = 500;
        return value.Length <= maxLength ? value : value[..maxLength];
    }
}
