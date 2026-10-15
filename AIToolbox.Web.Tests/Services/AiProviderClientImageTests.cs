using System.Net;
using System.Text;
using System.Text.Json;
using AIToolbox.Web.Models;
using AIToolbox.Web.Security;
using AIToolbox.Web.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace AIToolbox.Web.Tests.Services;

public class AiProviderClientImageTests
{
    private const string Key = "secret-key-value";

    private sealed class PassthroughProtector : ISecretProtector
    {
        public string? Protect(string? value) => value;
        public string? Unprotect(string? protectedValue) => protectedValue;
    }

    private sealed class Capture
    {
        public int Calls { get; set; }
        public HttpRequestMessage? Request { get; set; }
        public string? Body { get; set; }
        public JsonElement Json => JsonDocument.Parse(Body!).RootElement;
    }

    private static AiProviderClient Client(Capture capture, HttpStatusCode status = HttpStatusCode.OK, string response = "{}")
    {
        var factory = new StubHttpClientFactory(request =>
        {
            capture.Calls++;
            capture.Request = request;
            capture.Body = request.Content?.ReadAsStringAsync().GetAwaiter().GetResult();
            return new HttpResponseMessage(status) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        });
        return new AiProviderClient(factory, new PassthroughProtector(), NullLogger<AiProviderClient>.Instance);
    }

    private static AiProvider Provider(AiProviderType type, bool vision = true) => new()
    {
        Id = 7,
        DisplayName = "Vision test",
        ProviderType = type,
        EndpointUrl = "https://example.invalid/v1/messages",
        ModelName = "model-x",
        ApiKeyCipherText = Key,
        SupportsVision = vision,
        AllowedForDuctwork = true,
        GovernanceRoute = "Microsoft Foundry",
        MaxOutputTokens = 1000
    };

    private static ImagePromptImage Image(string mediaType = "image/png", int bytes = 16) =>
        new() { Bytes = Enumerable.Range(0, bytes).Select(i => (byte)i).ToArray(), MediaType = mediaType };

    private static ImagePromptRequest Request(AiProvider provider, params ImagePromptImage[] images) => new()
    {
        Provider = provider,
        Images = images.Length == 0 ? [Image()] : images,
        Directive = "Read the labels."
    };

    // ---- Request shapes

    [Fact]
    public async Task Anthropic_sends_image_blocks_before_the_text_and_reads_the_reply()
    {
        var capture = new Capture();
        var client = Client(capture, response: """{"content":[{"type":"text","text":"4x10x7"}],"stop_reason":"end_turn","usage":{"input_tokens":120,"output_tokens":5}}""");

        var result = await client.SendImagePromptAsync(Request(Provider(AiProviderType.Anthropic), Image("image/png"), Image("image/jpeg")));

        Assert.True(string.IsNullOrEmpty(result.ErrorMessage), result.ErrorMessage);
        Assert.Equal("4x10x7", result.ResponseText);
        Assert.Equal(120, result.InputTokens);
        Assert.Equal(5, result.OutputTokens);

        var content = capture.Json.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal(3, content.GetArrayLength());
        Assert.Equal("image", content[0].GetProperty("type").GetString());
        Assert.Equal("image/png", content[0].GetProperty("source").GetProperty("media_type").GetString());
        Assert.Equal("image/jpeg", content[1].GetProperty("source").GetProperty("media_type").GetString());
        Assert.Equal("text", content[2].GetProperty("type").GetString());
        Assert.Equal("Read the labels.", content[2].GetProperty("text").GetString());
        Assert.Equal(Key, capture.Request!.Headers.GetValues("x-api-key").Single());
        Assert.True(capture.Request.Headers.Contains("anthropic-version"));
    }

    [Fact]
    public async Task OpenAI_compatible_sends_data_url_image_parts()
    {
        var capture = new Capture();
        var client = Client(capture, response: """{"choices":[{"message":{"content":"ok"},"finish_reason":"stop"}],"usage":{"prompt_tokens":10,"completion_tokens":2}}""");
        var provider = Provider(AiProviderType.OpenAiCompatible);

        var result = await client.SendImagePromptAsync(Request(provider, Image("image/webp")));

        Assert.Equal("ok", result.ResponseText);
        var content = capture.Json.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal("text", content[0].GetProperty("type").GetString());
        Assert.Equal("image_url", content[1].GetProperty("type").GetString());
        Assert.StartsWith("data:image/webp;base64,", content[1].GetProperty("image_url").GetProperty("url").GetString());
        Assert.Equal("Bearer", capture.Request!.Headers.Authorization!.Scheme);
        Assert.Equal(Key, capture.Request.Headers.Authorization.Parameter);
        Assert.Equal(1000, capture.Json.GetProperty("max_tokens").GetInt32());
    }

    [Fact]
    public async Task Azure_uses_the_api_key_header()
    {
        var capture = new Capture();
        var client = Client(capture, response: """{"choices":[{"message":{"content":"ok"}}]}""");

        await client.SendImagePromptAsync(Request(Provider(AiProviderType.AzureOpenAi)));

        Assert.Equal(Key, capture.Request!.Headers.GetValues("api-key").Single());
        Assert.Null(capture.Request.Headers.Authorization);
    }

    [Fact]
    public async Task Responses_api_sends_input_image_and_input_text()
    {
        var capture = new Capture();
        var client = Client(capture, response: """{"output_text":"ok","status":"completed"}""");

        await client.SendImagePromptAsync(Request(Provider(AiProviderType.OpenAiResponses)));

        var content = capture.Json.GetProperty("input")[0].GetProperty("content");
        Assert.Equal("input_image", content[0].GetProperty("type").GetString());
        Assert.StartsWith("data:image/png;base64,", content[0].GetProperty("image_url").GetString());
        Assert.Equal("input_text", content[1].GetProperty("type").GetString());
        Assert.False(capture.Json.TryGetProperty("background", out _));
    }

    [Fact]
    public async Task The_api_key_never_appears_in_the_request_body()
    {
        foreach (var type in new[] { AiProviderType.Anthropic, AiProviderType.OpenAiCompatible, AiProviderType.AzureOpenAi, AiProviderType.OpenAiResponses })
        {
            var capture = new Capture();
            await Client(capture, response: """{"choices":[{"message":{"content":"ok"}}],"content":[{"text":"ok"}],"output_text":"ok"}""")
                .SendImagePromptAsync(Request(Provider(type)));
            Assert.DoesNotContain(Key, capture.Body!);
        }
    }

    // ---- Rejections happen before any network call

    [Fact]
    public async Task A_provider_that_is_not_vision_enabled_is_refused_and_nothing_is_sent()
    {
        var capture = new Capture();
        var ex = await Assert.ThrowsAsync<ImagePromptRejectedException>(() =>
            Client(capture).SendImagePromptAsync(Request(Provider(AiProviderType.Anthropic, vision: false))));

        Assert.Contains("not enabled for vision", ex.Message);
        Assert.Equal(0, capture.Calls);
    }

    [Theory]
    [InlineData(AiProviderType.OpenAiBatchResponses)]
    [InlineData(AiProviderType.AnthropicMessageBatch)]
    public async Task Batch_provider_types_have_no_image_route(AiProviderType type)
    {
        var capture = new Capture();
        await Assert.ThrowsAsync<ImagePromptRejectedException>(() =>
            Client(capture).SendImagePromptAsync(Request(Provider(type))));
        Assert.Equal(0, capture.Calls);
    }

    [Fact]
    public async Task Bad_image_sets_are_refused_and_nothing_is_sent()
    {
        var capture = new Capture();
        var client = Client(capture);
        var provider = Provider(AiProviderType.Anthropic);

        await Assert.ThrowsAsync<ImagePromptRejectedException>(() =>
            client.SendImagePromptAsync(new ImagePromptRequest { Provider = provider, Images = [], Directive = "x" }));
        await Assert.ThrowsAsync<ImagePromptRejectedException>(() =>
            client.SendImagePromptAsync(Request(provider, Image(bytes: 0))));
        await Assert.ThrowsAsync<ImagePromptRejectedException>(() =>
            client.SendImagePromptAsync(Request(provider, Image(bytes: ImagePromptLimits.MaxImageBytes + 1))));
        await Assert.ThrowsAsync<ImagePromptRejectedException>(() =>
            client.SendImagePromptAsync(Request(provider, Image("application/pdf"))));
        await Assert.ThrowsAsync<ImagePromptRejectedException>(() =>
            client.SendImagePromptAsync(Request(provider, Enumerable.Range(0, ImagePromptLimits.MaxImagesPerRequest + 1).Select(_ => Image()).ToArray())));
        await Assert.ThrowsAsync<ImagePromptRejectedException>(() =>
            client.SendImagePromptAsync(new ImagePromptRequest { Provider = provider, Images = [Image()], Directive = " " }));

        Assert.Equal(0, capture.Calls);
    }

    // ---- Provider failures come back as results, not exceptions

    [Fact]
    public async Task An_http_error_becomes_a_failed_result()
    {
        var capture = new Capture();
        var result = await Client(capture, HttpStatusCode.TooManyRequests, """{"error":"slow down"}""")
            .SendImagePromptAsync(Request(Provider(AiProviderType.Anthropic)));

        Assert.False(string.IsNullOrEmpty(result.ErrorMessage));
        Assert.Contains("429", result.ErrorMessage);
        Assert.Null(result.ResponseText);
        Assert.Equal(1, capture.Calls);
    }

    // ---- Snapshots and Ductwork eligibility

    [Fact]
    public void Snapshot_identifies_the_model_and_route_but_carries_no_secret()
    {
        var provider = Provider(AiProviderType.Anthropic);
        provider.ExtraHeadersJson = """{"x-secret":"s3cret"}""";
        provider.EndpointUrl = "https://Resource.Services.AI.Azure.com/anthropic/v1/messages?key=abc";

        var snapshot = provider.ToSnapshot();

        Assert.Equal("resource.services.ai.azure.com", snapshot.EndpointHost.ToLowerInvariant());
        Assert.True(snapshot.SupportsVision);
        Assert.Equal("Microsoft Foundry", snapshot.GovernanceRoute);
        var text = JsonSerializer.Serialize(snapshot);
        Assert.DoesNotContain("s3cret", text);
        Assert.DoesNotContain(Key, text);
        Assert.DoesNotContain("abc", text);
    }

    [Fact]
    public void Snapshot_hash_is_stable_and_changes_with_the_model_or_the_vision_flag()
    {
        var a = Provider(AiProviderType.Anthropic).ToSnapshot();
        var same = Provider(AiProviderType.Anthropic).ToSnapshot();
        var otherModel = a with { ModelName = "model-y" };
        var noVision = a with { SupportsVision = false };

        Assert.Equal(a.ComputeHash(), same.ComputeHash());
        Assert.NotEqual(a.ComputeHash(), otherModel.ComputeHash());
        Assert.NotEqual(a.ComputeHash(), noVision.ComputeHash());
        Assert.Equal(64, a.ComputeHash().Length);
    }

    [Fact]
    public void A_relative_endpoint_cannot_be_snapshotted()
    {
        var provider = Provider(AiProviderType.Anthropic);
        provider.EndpointUrl = "/relative";
        Assert.Throws<InvalidOperationException>(() => provider.ToSnapshot());
    }

    [Theory]
    [InlineData(true, true, true, "Microsoft Foundry", false, null)]
    [InlineData(true, true, true, null, false, null)]                       // text-only work needs no route
    [InlineData(false, true, true, "Microsoft Foundry", false, "disabled")]
    [InlineData(true, false, true, "Microsoft Foundry", false, "not been allowed")]
    [InlineData(true, true, false, "Microsoft Foundry", true, "not vision-enabled")]
    [InlineData(true, true, true, null, true, "governance route")]
    [InlineData(true, true, true, "  ", true, "governance route")]
    [InlineData(true, true, true, "Microsoft Foundry", true, null)]
    public void Ductwork_eligibility_reflects_enablement_allowance_vision_and_route(
        bool enabled, bool allowed, bool vision, string? route, bool needsVision, string? expectedReasonFragment)
    {
        var provider = Provider(AiProviderType.Anthropic);
        provider.IsEnabled = enabled;
        provider.AllowedForDuctwork = allowed;
        provider.SupportsVision = vision;
        provider.GovernanceRoute = route;

        var reason = provider.WhyNotUsableForDuctwork(needsVision);

        if (expectedReasonFragment is null) Assert.Null(reason);
        else Assert.Contains(expectedReasonFragment, reason);
    }

    [Fact]
    public void New_providers_default_to_no_vision_and_not_allowed_for_ductwork()
    {
        var provider = new AiProvider();
        Assert.False(provider.SupportsVision);
        Assert.False(provider.AllowedForDuctwork);
        Assert.Null(provider.GovernanceRoute);
    }
}
