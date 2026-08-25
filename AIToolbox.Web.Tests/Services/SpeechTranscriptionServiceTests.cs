using System.Net;
using AIToolbox.Web.Models;
using AIToolbox.Web.Security;
using AIToolbox.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIToolbox.Web.Tests.Services;

public sealed class SpeechTranscriptionServiceTests
{
    private static ExternalResource CreateResource() => new()
    {
        ResourceKey = "azure-speech",
        DisplayName = "Azure Speech",
        ConfigJson = """{"region":"eastus","language":"en-US"}""",
        SecretCipherText = "protected-key",
        IsEnabled = true,
    };

    [Fact]
    public async Task TranscribeAsync_TranscoderFailure_NeverCallsAzureOrArchive()
    {
        var azureWasCalled = false;
        var httpClientFactory = new StubHttpClientFactory(_ =>
        {
            azureWasCalled = true;
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var transcoder = new FakeSpeechAudioTranscoder(SpeechTranscodeResult.Failure("Couldn't process that recording."));
        var archive = new FakeSpeechAudioArchiveService();
        var service = new SpeechTranscriptionService(
            httpClientFactory, new PassthroughSecretProtector(), transcoder, archive, NullLogger<SpeechTranscriptionService>.Instance);

        var result = await service.TranscribeAsync(CreateResource(), new MemoryStream([1, 2, 3]), "audio/webm;codecs=opus");

        Assert.False(result.Succeeded);
        Assert.False(azureWasCalled);
        Assert.False(archive.WasCalled);
    }

    [Fact]
    public async Task TranscribeAsync_TranscoderSucceeds_ArchivesThenCallsAzure()
    {
        var wavBytes = new byte[] { 0x52, 0x49, 0x46, 0x46 }; // "RIFF"
        var httpClientFactory = new StubHttpClientFactory(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"RecognitionStatus":"Success","DisplayText":"hello"}"""),
        });
        var transcoder = new FakeSpeechAudioTranscoder(SpeechTranscodeResult.Success(wavBytes));
        var archive = new FakeSpeechAudioArchiveService();
        var service = new SpeechTranscriptionService(
            httpClientFactory, new PassthroughSecretProtector(), transcoder, archive, NullLogger<SpeechTranscriptionService>.Instance);

        var result = await service.TranscribeAsync(CreateResource(), new MemoryStream(wavBytes), "audio/webm;codecs=opus");

        Assert.True(archive.WasCalled);
        Assert.True(result.Succeeded);
        Assert.Equal("hello", result.Text);
    }

    [Fact]
    public async Task TranscribeAsync_AlwaysSendsFixedWavContentTypeToAzure_RegardlessOfClientContentType()
    {
        HttpRequestMessage? capturedRequest = null;
        var wavBytes = new byte[] { 0x52, 0x49, 0x46, 0x46 };
        var httpClientFactory = new StubHttpClientFactory(request =>
        {
            capturedRequest = request;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("""{"RecognitionStatus":"Success","DisplayText":"hi"}"""),
            };
        });
        var transcoder = new FakeSpeechAudioTranscoder(SpeechTranscodeResult.Success(wavBytes));
        var archive = new FakeSpeechAudioArchiveService();
        var service = new SpeechTranscriptionService(
            httpClientFactory, new PassthroughSecretProtector(), transcoder, archive, NullLogger<SpeechTranscriptionService>.Instance);

        // Client claims webm/opus — proves the service no longer trusts or forwards that value.
        await service.TranscribeAsync(CreateResource(), new MemoryStream(wavBytes), "audio/webm;codecs=opus");

        Assert.NotNull(capturedRequest);
        // .ContentType (the typed accessor) can't represent this value — see the comment in
        // SpeechTranscriptionService.cs — so read the raw header string back instead.
        var contentTypeValues = capturedRequest!.Content!.Headers.GetValues("Content-Type");
        Assert.Equal("audio/wav; codecs=audio/pcm; samplerate=16000", Assert.Single(contentTypeValues));
    }

    private sealed class FakeSpeechAudioTranscoder(SpeechTranscodeResult result) : ISpeechAudioTranscoder
    {
        public Task<SpeechTranscodeResult> TranscodeToWavAsync(Stream input, CancellationToken cancellationToken = default) =>
            Task.FromResult(result);
    }

    private sealed class FakeSpeechAudioArchiveService : ISpeechAudioArchiveService
    {
        public bool WasCalled { get; private set; }

        public Task SaveAsync(byte[] wavBytes, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            return Task.CompletedTask;
        }
    }

    private sealed class PassthroughSecretProtector : ISecretProtector
    {
        public string? Protect(string? value) => value;

        public string? Unprotect(string? protectedValue) => protectedValue;
    }
}
