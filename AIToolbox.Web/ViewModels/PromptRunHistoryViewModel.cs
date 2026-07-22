namespace AIToolbox.Web.ViewModels;

public sealed class PromptRunHistoryViewModel
{
    public string Scope { get; set; } = "all";

    public IReadOnlyList<PromptRunHistoryItemViewModel> Runs { get; set; } = [];
}
