using AIToolbox.Web.Models;

namespace AIToolbox.Web.ViewModels;

public sealed class AnalysisRunViewModel
{
    public PromptRun PromptRun { get; set; } = new();

    public AnalysisRun AnalysisRun { get; set; } = new();
}
