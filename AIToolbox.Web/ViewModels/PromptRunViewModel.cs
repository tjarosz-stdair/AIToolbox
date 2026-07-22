using AIToolbox.Web.Models;

namespace AIToolbox.Web.ViewModels;

public sealed class PromptRunViewModel
{
    public PromptRun Run { get; set; } = new();

    public bool IsOwner { get; set; }

    public IReadOnlyList<AnalysisRunSummary> AnalysisRuns { get; set; } = [];

    public List<ProviderCheckboxViewModel> AnalysisProviders { get; set; } = [];
}
