namespace AIToolbox.Web.ViewModels;

public sealed class AnalyzeRequestViewModel
{
    public int PromptRunId { get; set; }

    public List<int> SelectedProviderIds { get; set; } = [];
}
