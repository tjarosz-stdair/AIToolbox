using System.Text.Json;

namespace AIToolbox.Web.Models;

public sealed class CreateAiBatchJobRequest
{
    public string ToolKey { get; set; } = string.Empty;
    public string JobType { get; set; } = "BatchAnalysis";
    public int AiProviderId { get; set; }
    public string InputMode { get; set; } = "json";
    public JsonElement? Metadata { get; set; }
    public string? CallbackUrl { get; set; }
    public IReadOnlyList<CreateAiBatchRequestItem> Requests { get; set; } = [];
}

public sealed class CreateAiBatchRequestItem
{
    public string? CustomId { get; set; }
    public string? SourceObjectType { get; set; }
    public string? SourceObjectId { get; set; }
    public JsonElement Body { get; set; }
}

public sealed class CreateAiBatchJobResult
{
    public int Id { get; set; }
    public string Status { get; set; } = string.Empty;
    public int TotalRequestCount { get; set; }
}
