namespace AIToolbox.Web.Models;

public sealed class PromptRunSummary
{
    public int Id { get; set; }

    public string UserEmail { get; set; } = string.Empty;

    public string Question { get; set; } = string.Empty;

    public bool IsPublic { get; set; }

    public DateTime CreatedUtc { get; set; }

    public int ResponseCount { get; set; }

    public int AnalysisRunCount { get; set; }

    public DateTime? LastAnalysisUtc { get; set; }
}
