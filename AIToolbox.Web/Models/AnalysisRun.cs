namespace AIToolbox.Web.Models;

public sealed class AnalysisRun
{
    public int Id { get; set; }

    public int PromptRunId { get; set; }

    public string UserEmail { get; set; } = string.Empty;

    public string AnalysisPrompt { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; }

    public List<AnalysisResponse> Responses { get; set; } = [];
}
