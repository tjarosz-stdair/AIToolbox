using AIToolbox.Web.Models;

namespace AIToolbox.Web.ViewModels;

public sealed class ParsedProductDetailViewModel
{
    public required ParsedProduct Product { get; set; }

    public ProductParseJob? Job { get; set; }

    public IReadOnlyList<ParsedProduct> Versions { get; set; } = [];
}
