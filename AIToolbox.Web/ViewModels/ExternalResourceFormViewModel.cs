using System.ComponentModel.DataAnnotations;
using AIToolbox.Web.Models;
using Microsoft.AspNetCore.Mvc.Rendering;

namespace AIToolbox.Web.ViewModels;

public sealed class ExternalResourceFormViewModel
{
    // Named form (not Html.GetEnumSelectList's numeric-value form) so the client-side
    // data-resource-type-field/-help toggle in _Form.cshtml can match on it directly. Exposed via
    // asp-items rather than hand-written <option selected="@(...)"> markup -- that combination
    // with asp-for is a known-fragile pattern for round-tripping the selected value through a
    // failed-validation re-render; asp-items is the framework's tested path for both directions.
    // Static: it's a fixed option list, not per-instance form data, so the model binder never
    // touches it regardless.
    public static IReadOnlyList<SelectListItem> ResourceTypeOptions { get; } = new[]
    {
        new SelectListItem("Azure Speech-to-Text", nameof(ExternalResourceType.AzureSpeech)),
        new SelectListItem("Call Data API", nameof(ExternalResourceType.CallDataApi))
    };

    public int Id { get; set; }

    [Display(Name = "Resource type")]
    public ExternalResourceType ResourceType { get; set; } = ExternalResourceType.AzureSpeech;

    [Required]
    [StringLength(150)]
    [Display(Name = "Display name")]
    public string DisplayName { get; set; } = "Azure Speech-to-Text";

    // Only required/used when ResourceType == AzureSpeech -- enforced in the controller, not via
    // [Required], since which fields matter depends on the selected ResourceType. Must be a
    // nullable string, not just "no [Required] attribute" -- with <Nullable>enable</Nullable> on
    // (as this project has), MVC implicitly treats any non-nullable string property as required
    // regardless of DataAnnotations, which silently overrode the controller's resource-type-scoped
    // check on every submission (BaseUrl/SubscriptionKey below are already nullable for the same
    // reason).
    [StringLength(60)]
    [Display(Name = "Azure region")]
    public string? Region { get; set; } = string.Empty;

    [StringLength(20)]
    [Display(Name = "Recognition language")]
    public string? Language { get; set; } = "en-US";

    // Only required/used when ResourceType == CallDataApi.
    [StringLength(300)]
    [Display(Name = "Call Data API base URL")]
    public string? BaseUrl { get; set; }

    [DataType(DataType.Password)]
    [Display(Name = "Subscription key / secret")]
    public string? SubscriptionKey { get; set; }

    [Display(Name = "Clear stored key")]
    public bool ClearSecret { get; set; }

    [Display(Name = "Enabled")]
    public bool IsEnabled { get; set; } = true;

    public bool HasStoredSecret { get; set; }
}
