namespace AIToolbox.Web.Models;

public sealed class DocumentPromptRequest
{
    public required AiProvider Provider { get; init; }
    public byte[]? DocumentBytes { get; init; }
    public string? FileName { get; init; }
    public string? MediaType { get; init; }
    public IReadOnlyList<DocumentPromptFile> Documents { get; init; } = [];
    public required string Directive { get; init; }
}

public sealed class DocumentPromptFile
{
    public required byte[] Bytes { get; init; }
    public required string FileName { get; init; }
    public required string MediaType { get; init; }
}

public sealed class DocumentPromptResult
{
    public int ProviderId { get; set; }
    public string ProviderName { get; set; } = string.Empty;
    public string? ResponseText { get; set; }
    public string? ErrorMessage { get; set; }
    public string? FinishReason { get; set; }
    public int? InputTokens { get; set; }
    public int? OutputTokens { get; set; }
    public int DurationMs { get; set; }
}
