using System.ComponentModel.DataAnnotations;
using AIToolbox.Web.Models;

namespace AIToolbox.Web.ViewModels;

public sealed class ExternalResourceFormViewModel
{
    public int Id { get; set; }

    [Display(Name = "Resource type")]
    public ExternalResourceType ResourceType { get; set; } = ExternalResourceType.AzureSpeech;

    [Required]
    [StringLength(150)]
    [Display(Name = "Display name")]
    public string DisplayName { get; set; } = "Azure Speech-to-Text";

    // Only required/used when ResourceType == AzureSpeech -- enforced in the controller, not via
    // [Required], since which fields matter depends on the selected ResourceType.
    [StringLength(60)]
    [Display(Name = "Azure region")]
    public string Region { get; set; } = string.Empty;

    [StringLength(20)]
    [Display(Name = "Recognition language")]
    public string Language { get; set; } = "en-US";

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
