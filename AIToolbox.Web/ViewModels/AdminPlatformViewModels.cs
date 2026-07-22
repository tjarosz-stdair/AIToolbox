using System.ComponentModel.DataAnnotations;
using AIToolbox.Web.Models;

namespace AIToolbox.Web.ViewModels;

public sealed class ToolEditViewModel
{
    public int Id { get; set; }

    [Required, StringLength(80)]
    public string ToolKey { get; set; } = string.Empty;

    [Required, StringLength(120)]
    public string DisplayName { get; set; } = string.Empty;

    [StringLength(1000)]
    public string? Description { get; set; }

    public bool IsEnabled { get; set; } = true;

    public ToolAccessMode AccessMode { get; set; }

    public int? ActiveProviderId { get; set; }

    public IReadOnlyList<AiProvider> Providers { get; set; } = [];

    public IReadOnlyList<ToolUserAllowListEntry> AllowList { get; set; } = [];

    [EmailAddress, StringLength(320)]
    public string? NewAllowListEmail { get; set; }
}

public sealed class DirectiveEditViewModel
{
    public int Id { get; set; }

    [Required, StringLength(80)]
    public string ToolKey { get; set; } = "ProductParser";

    [Required, StringLength(120)]
    public string Name { get; set; } = string.Empty;

    [Required]
    public string SystemPrompt { get; set; } = string.Empty;

    [Required]
    public string TargetSchemaJson { get; set; } = string.Empty;

    [Range(1, int.MaxValue)]
    public int Version { get; set; } = 1;

    public bool IsActive { get; set; } = true;
}

public sealed class UsageIndexViewModel
{
    public IReadOnlyList<ToolUsageLog> Usage { get; set; } = [];
    public IReadOnlyList<ModelPricing> Pricing { get; set; } = [];
    public string? ToolKey { get; set; }
    public string? UserEmail { get; set; }
}

public sealed class ModelPricingFormViewModel
{
    public AiProviderType ProviderType { get; set; }

    [Required, StringLength(200)]
    public string ModelName { get; set; } = string.Empty;

    [Range(0, 1000)]
    public decimal InputPricePer1kUsd { get; set; }

    [Range(0, 1000)]
    public decimal OutputPricePer1kUsd { get; set; }
}

public sealed class StorageIndexViewModel
{
    public string BasePath { get; set; } = string.Empty;
    public long MaxUploadBytes { get; set; }
    public IReadOnlyDictionary<string, string> ToolSubpaths { get; set; } = new Dictionary<string, string>();
}
