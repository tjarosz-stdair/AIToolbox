namespace AIToolbox.Web.Models;

public sealed class AnalysisRunSummary
{
    public int Id { get; set; }

    public int PromptRunId { get; set; }

    public string UserEmail { get; set; } = string.Empty;

    public DateTime CreatedUtc { get; set; }

    public int ResponseCount { get; set; }
}
