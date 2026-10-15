using System.ComponentModel.DataAnnotations;
using AIToolbox.Web.Models;

namespace AIToolbox.Web.ViewModels;

public sealed class ProviderFormViewModel
{
    public int Id { get; set; }

    [Required]
    [StringLength(100)]
    [Display(Name = "Display name")]
    public string DisplayName { get; set; } = string.Empty;

    [Display(Name = "Provider type")]
    public AiProviderType ProviderType { get; set; } = AiProviderType.OpenAiCompatible;

    [Required]
    [StringLength(1000)]
    [Url]
    [Display(Name = "Endpoint URL")]
    public string EndpointUrl { get; set; } = string.Empty;

    [Required]
    [StringLength(200)]
    [Display(Name = "Model name")]
    public string ModelName { get; set; } = string.Empty;

    [StringLength(100)]
    [Display(Name = "API version")]
    public string? ApiVersion { get; set; }

    [DataType(DataType.Password)]
    [Display(Name = "API key or token")]
    public string? ApiKey { get; set; }

    [Display(Name = "Clear stored key")]
    public bool ClearApiKey { get; set; }

    [Display(Name = "Extra headers JSON")]
    public string? ExtraHeadersJson { get; set; }

    [Range(1, 200000)]
    [Display(Name = "Max output tokens")]
    public int? MaxOutputTokens { get; set; }

    [Range(5, 600)]
    [Display(Name = "Timeout seconds")]
    public int RequestTimeoutSeconds { get; set; } = 120;

    [Display(Name = "Enabled")]
    public bool IsEnabled { get; set; } = true;

    [Display(Name = "Supports vision (image input)")]
    public bool SupportsVision { get; set; }

    [Display(Name = "Allowed for Ductwork")]
    public bool AllowedForDuctwork { get; set; }

    [StringLength(100)]
    [Display(Name = "Governance route")]
    public string? GovernanceRoute { get; set; }

    public bool HasStoredApiKey { get; set; }
}
