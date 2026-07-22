namespace AIToolbox.Web.ViewModels;

public sealed class ProviderCheckboxViewModel
{
    public int Id { get; set; }

    public string DisplayName { get; set; } = string.Empty;

    public string ProviderType { get; set; } = string.Empty;

    public string ModelName { get; set; } = string.Empty;

    public bool IsChecked { get; set; }
}
