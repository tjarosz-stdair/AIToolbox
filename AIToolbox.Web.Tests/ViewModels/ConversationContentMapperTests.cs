using AIToolbox.Web.ViewModels;
using Microsoft.Extensions.AI;

namespace AIToolbox.Web.Tests.ViewModels;

public sealed class ConversationContentMapperTests
{
    [Fact]
    public void Map_TextContent_ProducesTextDisplay()
    {
        var display = ConversationContentMapper.Map([new TextContent("hello there")]);

        var text = Assert.IsType<ConversationContentDisplay.Text>(Assert.Single(display));
        Assert.Equal("hello there", text.Value);
    }

    [Fact]
    public void Map_FunctionCallContent_ProducesToolCallDisplay()
    {
        var arguments = new Dictionary<string, object?> { ["salesPackageId"] = "ABC123" };
        var display = ConversationContentMapper.Map([new FunctionCallContent("call-1", "get_product_specs", arguments)]);

        var toolCall = Assert.IsType<ConversationContentDisplay.ToolCall>(Assert.Single(display));
        Assert.Equal("get_product_specs", toolCall.Name);
        Assert.Equal("call-1", toolCall.CallId);
        Assert.Contains("ABC123", toolCall.ArgumentsJson);
    }

    [Fact]
    public void Map_FunctionResultContent_ProducesToolResultDisplay()
    {
        var display = ConversationContentMapper.Map([new FunctionResultContent("call-1", "some product data")]);

        var toolResult = Assert.IsType<ConversationContentDisplay.ToolResult>(Assert.Single(display));
        Assert.Equal("call-1", toolResult.CallId);
        Assert.False(toolResult.IsError);
        Assert.Contains("some product data", toolResult.ResultJson);
    }

    [Fact]
    public void Map_FunctionResultContent_WithExceptionResult_IsMarkedAsError()
    {
        var display = ConversationContentMapper.Map([new FunctionResultContent("call-1", new InvalidOperationException("boom"))]);

        var toolResult = Assert.IsType<ConversationContentDisplay.ToolResult>(Assert.Single(display));
        Assert.True(toolResult.IsError);
    }

    [Fact]
    public void Map_UnrecognizedContentType_FallsBackToRaw()
    {
        var display = ConversationContentMapper.Map([new UsageContent(new UsageDetails { InputTokenCount = 10, OutputTokenCount = 5 })]);

        var raw = Assert.IsType<ConversationContentDisplay.Raw>(Assert.Single(display));
        Assert.Equal(nameof(UsageContent), raw.TypeName);
        Assert.NotEmpty(raw.Json);
    }
}
