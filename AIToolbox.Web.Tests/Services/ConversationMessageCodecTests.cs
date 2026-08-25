using AIToolbox.Web.Services;
using Microsoft.Extensions.AI;

namespace AIToolbox.Web.Tests.Services;

public sealed class ConversationMessageCodecTests
{
    [Fact]
    public void SerializeThenDeserialize_RoundTripsTextContent()
    {
        var message = new ChatMessage(ChatRole.User, [new TextContent("what's the status of order 12345?")]);

        var stored = ConversationMessageCodec.Serialize(conversationId: 1, message, sequence: 0, modelId: "gpt-test", aiProviderId: 7);
        var restored = ConversationMessageCodec.Deserialize(stored);

        Assert.Equal("user", stored.Role);
        Assert.Equal(1, stored.ConversationId);
        Assert.Equal(0, stored.Sequence);
        Assert.Equal("gpt-test", stored.ModelId);
        Assert.Equal(7, stored.AiProviderId);

        var text = Assert.IsType<TextContent>(Assert.Single(restored.Contents));
        Assert.Equal("what's the status of order 12345?", text.Text);
    }

    [Fact]
    public void SerializeThenDeserialize_RoundTripsFunctionCallContent()
    {
        var arguments = new Dictionary<string, object?> { ["salesPackageId"] = "ABC123" };
        var message = new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call-1", "get_product_specs", arguments)]);

        var stored = ConversationMessageCodec.Serialize(conversationId: 1, message, sequence: 1, modelId: null, aiProviderId: null);
        var restored = ConversationMessageCodec.Deserialize(stored);

        var call = Assert.IsType<FunctionCallContent>(Assert.Single(restored.Contents));
        Assert.Equal("call-1", call.CallId);
        Assert.Equal("get_product_specs", call.Name);
        Assert.Equal("ABC123", call.Arguments?["salesPackageId"]?.ToString());
    }

    [Fact]
    public void SerializeThenDeserialize_RoundTripsFunctionResultContent()
    {
        var message = new ChatMessage(ChatRole.Tool, [new FunctionResultContent("call-1", "some product data")]);

        var stored = ConversationMessageCodec.Serialize(conversationId: 1, message, sequence: 2, modelId: null, aiProviderId: null);
        var restored = ConversationMessageCodec.Deserialize(stored);

        var result = Assert.IsType<FunctionResultContent>(Assert.Single(restored.Contents));
        Assert.Equal("call-1", result.CallId);
        Assert.Equal("some product data", result.Result?.ToString());
    }
}
