using AIToolbox.Web.Models;

namespace AIToolbox.Web.ViewModels;

public sealed class ProductParseJobDetailViewModel
{
    public required ToolJob Job { get; set; }

    public IReadOnlyList<ProductParseJob> Files { get; set; } = [];

    public IReadOnlyList<ParsedProduct> Products { get; set; } = [];

    public int? SelectedProductId { get; set; }

    public ParsedProduct? SelectedProduct { get; set; }
}
