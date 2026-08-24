using System.Text.Json;
using AIToolbox.Web.Models;
using AIToolbox.Web.Services;
using Microsoft.Extensions.AI;

namespace AIToolbox.Web.Tests.Services;

public sealed class PicSearchChatServiceTests
{
    private static UiIntentFunction CreateUiTool(string name = "ui_show_part", string uiSurface = "picsearch")
    {
        using var schema = JsonDocument.Parse("""{"type":"object","properties":{}}""");
        var capability = new UiCapability
        {
            Name = name,
            Description = "Shows a part on screen. Returns no product data — use get_product_specs for that.",
            Kind = "navigation",
            InputSchema = schema.RootElement.Clone(),
            UiSurface = uiSurface
        };
        return new UiIntentFunction(capability);
    }

    /// <summary>
    /// Directly verifies the assumption ExtractQueuedUiIntents leans on: that
    /// Microsoft.Extensions.AI's FunctionInvokingChatClient passes the raw object? a function
    /// invocation returns straight through to FunctionResultContent.Result, with no intermediate
    /// (e.g. JSON round-trip) transformation. This was reasoned from AiChatClient.ResultToText's
    /// own handling, but never directly checked against the real framework until now.
    /// </summary>
    [Fact]
    public async Task FunctionInvokingChatClient_PassesRawInvokerResultThroughUnchanged()
    {
        var uiTool = CreateUiTool();
        var chatClient = new ChatClientBuilder(new ScriptedToolCallingChatClient(uiTool.Name))
            .UseFunctionInvocation()
            .Build();

        var response = await chatClient.GetResponseAsync(
            [new ChatMessage(ChatRole.User, "show me the part")],
            new ChatOptions { Tools = [uiTool] });

        var resultContent = response.Messages.SelectMany(m => m.Contents.OfType<FunctionResultContent>()).Single();

        var resultDictionary = Assert.IsAssignableFrom<IDictionary<string, object?>>(resultContent.Result);
        Assert.Equal("queued", resultDictionary["status"]);
    }

    [Fact]
    public void ExtractQueuedUiIntents_IncludesAQueuedUiCall()
    {
        var uiTool = CreateUiTool();
        var toolsByName = new Dictionary<string, UiIntentFunction> { [uiTool.Name] = uiTool };
        var messages = BuildTurn(
            (uiTool.Name, "call-1", new Dictionary<string, object?> { ["salesPackageId"] = "ABC123" }, Queued()));

        var intents = PicSearchChatService.ExtractQueuedUiIntents(messages, toolsByName);

        var intent = Assert.Single(intents);
        Assert.Equal(uiTool.Name, intent.Name);
        Assert.Equal("navigation", intent.Kind);
        Assert.Equal("picsearch", intent.Surface);
        Assert.Equal("ABC123", intent.Arguments["salesPackageId"]);
    }

    [Fact]
    public void ExtractQueuedUiIntents_ExcludesACallTheCapRejected()
    {
        var uiTool = CreateUiTool();
        var toolsByName = new Dictionary<string, UiIntentFunction> { [uiTool.Name] = uiTool };
        var messages = BuildTurn(
            (uiTool.Name, "call-1", new Dictionary<string, object?>(), Rejected()));

        var intents = PicSearchChatService.ExtractQueuedUiIntents(messages, toolsByName);

        Assert.Empty(intents);
    }

    [Fact]
    public void ExtractQueuedUiIntents_IgnoresNonUiToolCalls()
    {
        var uiTool = CreateUiTool();
        var toolsByName = new Dictionary<string, UiIntentFunction> { [uiTool.Name] = uiTool };
        var messages = new List<ChatMessage>
        {
            new(ChatRole.Assistant, [new FunctionCallContent("call-1", "get_product_specs", new Dictionary<string, object?>())]),
            new(ChatRole.Tool, [new FunctionResultContent("call-1", "some product data")])
        };

        var intents = PicSearchChatService.ExtractQueuedUiIntents(messages, toolsByName);

        Assert.Empty(intents);
    }

    [Fact]
    public void ExtractQueuedUiIntents_PreservesOrderAcrossMultipleQueuedCalls()
    {
        var toolA = CreateUiTool("ui_show_part");
        var toolB = CreateUiTool("ui_show_model_search");
        var toolsByName = new Dictionary<string, UiIntentFunction>
        {
            [toolA.Name] = toolA,
            [toolB.Name] = toolB
        };
        var messages = BuildTurn(
            (toolA.Name, "call-1", new Dictionary<string, object?>(), Queued()),
            (toolB.Name, "call-2", new Dictionary<string, object?>(), Queued()));

        var intents = PicSearchChatService.ExtractQueuedUiIntents(messages, toolsByName);

        Assert.Equal(2, intents.Count);
        Assert.Equal(toolA.Name, intents[0].Name);
        Assert.Equal(toolB.Name, intents[1].Name);
    }

    private static Dictionary<string, object?> Queued() => new() { ["status"] = "queued" };

    private static Dictionary<string, object?> Rejected() => new() { ["status"] = "rejected", ["reason"] = "cap exceeded" };

    private static List<ChatMessage> BuildTurn(params (string ToolName, string CallId, Dictionary<string, object?> Arguments, Dictionary<string, object?> Result)[] calls)
    {
        var callContents = calls.Select(c => (AIContent)new FunctionCallContent(c.CallId, c.ToolName, c.Arguments)).ToList();
        var resultContents = calls.Select(c => (AIContent)new FunctionResultContent(c.CallId, c.Result)).ToList();

        return
        [
            new ChatMessage(ChatRole.Assistant, callContents),
            new ChatMessage(ChatRole.Tool, resultContents)
        ];
    }

    /// <summary>Returns a tool call for toolName on the first turn, then a final text response
    /// once it sees the tool result come back — mimicking a real provider's multi-step behavior
    /// so FunctionInvokingChatClient actually drives its real invocation loop.</summary>
    private sealed class ScriptedToolCallingChatClient : IChatClient
    {
        private readonly string _toolName;

        public ScriptedToolCallingChatClient(string toolName)
        {
            _toolName = toolName;
        }

        public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
        {
            var alreadyHasResult = messages.Any(m => m.Contents.OfType<FunctionResultContent>().Any());
            if (!alreadyHasResult)
            {
                var callContent = new FunctionCallContent("call-1", _toolName, new Dictionary<string, object?> { ["salesPackageId"] = "ABC123" });
                return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [callContent])));
            }

            return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, [new TextContent("Here you go.")])));
        }

        public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();

        public object? GetService(Type serviceType, object? serviceKey = null) => null;

        public void Dispose()
        {
        }
    }
}
