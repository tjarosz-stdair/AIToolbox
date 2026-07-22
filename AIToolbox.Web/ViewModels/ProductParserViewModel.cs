using System.ComponentModel.DataAnnotations;
using AIToolbox.Web.Models;
using Microsoft.AspNetCore.Http;

namespace AIToolbox.Web.ViewModels;

public sealed class ProductParserViewModel
{
    [Display(Name = "Source files")]
    public List<IFormFile> Files { get; set; } = [];

    [Display(Name = "Share this parse with authorized users")]
    public bool IsPublic { get; set; } = true;

    [Display(Name = "Upload Intent")]
    public string ParseMode { get; set; } = "sameProduct";

    [Display(Name = "Additional Directives")]
    public string? UserNotes { get; set; }

    [Display(Name = "Directive")]
    public int? SelectedDirectiveId { get; set; }

    public IReadOnlyList<ToolDirective> AvailableDirectives { get; set; } = [];

    public string Scope { get; set; } = "all";

    public string ViewMode { get; set; } = "new";

    public string CurrentUserEmail { get; set; } = string.Empty;

    public int JobPage { get; set; } = 1;

    public int JobPageSize { get; set; } = 20;

    public string JobSortBy { get; set; } = "date";

    public string JobSortDirection { get; set; } = "desc";

    public string? Search { get; set; }

    public string? Supplier { get; set; }

    public string? Manufacturer { get; set; }

    public string? Category { get; set; }

    public decimal? MinValue { get; set; }

    public decimal? MaxValue { get; set; }

    public IReadOnlyList<ProductParseJob> Jobs { get; set; } = [];

    public ProductParseJobHistoryResult JobHistory { get; set; } = new();

    public IReadOnlyList<ParsedProduct> Products { get; set; } = [];

    public IReadOnlyList<ProductLibraryItem> LibraryItems { get; set; } = [];

    public IReadOnlyList<ProductLibraryGroup> LibraryGroups { get; set; } = [];

    public IReadOnlyList<string> SupplierOptions { get; set; } = [];

    public IReadOnlyList<string> ManufacturerOptions { get; set; } = [];

    public IReadOnlyList<string> CategoryOptions { get; set; } = [];
}
