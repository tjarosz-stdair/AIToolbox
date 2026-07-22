namespace AIToolbox.Web.Models;

public sealed class AnalysisResponse
{
    public int Id { get; set; }

    public int AnalysisRunId { get; set; }

    public int AiProviderId { get; set; }

    public string ProviderName { get; set; } = string.Empty;

    public string? ResponseText { get; set; }

    public string? ErrorMessage { get; set; }

    public string? FinishReason { get; set; }

    public int? InputTokens { get; set; }

    public int? OutputTokens { get; set; }

    public int DurationMs { get; set; }

    public DateTime CreatedUtc { get; set; }
}
