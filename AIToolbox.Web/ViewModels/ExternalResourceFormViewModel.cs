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

    [Required]
    [StringLength(60)]
    [Display(Name = "Azure region")]
    public string Region { get; set; } = string.Empty;

    [Required]
    [StringLength(20)]
    [Display(Name = "Recognition language")]
    public string Language { get; set; } = "en-US";

    [DataType(DataType.Password)]
    [Display(Name = "Subscription key")]
    public string? SubscriptionKey { get; set; }

    [Display(Name = "Clear stored key")]
    public bool ClearSecret { get; set; }

    [Display(Name = "Enabled")]
    public bool IsEnabled { get; set; } = true;

    public bool HasStoredSecret { get; set; }
}
