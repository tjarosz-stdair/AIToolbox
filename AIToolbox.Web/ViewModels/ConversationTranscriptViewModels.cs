using System.Text.Json;
using AIToolbox.Web.Models;
using Microsoft.Extensions.AI;

namespace AIToolbox.Web.ViewModels;

/// <summary>
/// A small closed hierarchy so Razor pattern-matches display records instead of touching
/// AIContent or ContentJson's raw, type-discriminator-tagged JSON directly.
/// </summary>
public abstract record ConversationContentDisplay
{
    public sealed record Text(string Value) : ConversationContentDisplay;

    public sealed record ToolCall(string Name, string CallId, string ArgumentsJson) : ConversationContentDisplay;

    public sealed record ToolResult(string CallId, string ResultJson, bool IsError) : ConversationContentDisplay;

    /// <summary>Fallback for anything unrecognized (UsageContent, ErrorContent, DataContent, a
    /// future AIContent subtype, ...) — nothing silently disappears from the transcript, it just
    /// renders less prettily.</summary>
    public sealed record Raw(string TypeName, string Json) : ConversationContentDisplay;
}

public static class ConversationContentMapper
{
    public static IReadOnlyList<ConversationContentDisplay> Map(IEnumerable<AIContent> contents)
    {
        var results = new List<ConversationContentDisplay>();
        foreach (var content in contents)
        {
            results.Add(content switch
            {
                TextContent text => new ConversationContentDisplay.Text(text.Text),
                FunctionCallContent call => new ConversationContentDisplay.ToolCall(call.Name, call.CallId, SafeSerialize(call.Arguments)),
                FunctionResultContent result => new ConversationContentDisplay.ToolResult(result.CallId, SafeSerialize(result.Result), result.Result is Exception),
                _ => new ConversationContentDisplay.Raw(content.GetType().Name, SafeSerialize(content))
            });
        }

        return results;
    }

    // Historical rows can carry arbitrary/opaque payloads (an MCP tool's own result shape) —
    // one bad row's serialization failure shouldn't take down the whole transcript view.
    private static string SafeSerialize(object? value)
    {
        if (value is null)
        {
            return "null";
        }

        try
        {
            return JsonSerializer.Serialize(value, AIJsonUtilities.DefaultOptions);
        }
        catch (Exception)
        {
            return value.ToString() ?? "null";
        }
    }
}

public sealed class ConversationDetailViewModel
{
    public required Conversation Conversation { get; init; }

    public required List<(ConversationMessage Message, IReadOnlyList<ConversationContentDisplay> Contents)> Transcript { get; init; }

    public int RetentionDays { get; init; }
}

public sealed class UsageRollupSummaryViewModel
{
    public IReadOnlyList<ConversationUsageRollup> Rows { get; init; } = [];

    public required ConversationUsageRollup Totals { get; init; }

    public DateOnly From { get; init; }

    public DateOnly To { get; init; }

    public string Surface { get; init; } = "all";

    public IReadOnlyList<string> Surfaces { get; init; } = [];
}
