using System.ComponentModel.DataAnnotations;

namespace AIToolbox.Web.Models;

public sealed class AiProvider
{
    public int Id { get; set; }

    [Required]
    [StringLength(100)]
    public string DisplayName { get; set; } = string.Empty;

    public AiProviderType ProviderType { get; set; } = AiProviderType.OpenAiCompatible;

    [Required]
    [StringLength(1000)]
    public string EndpointUrl { get; set; } = string.Empty;

    [Required]
    [StringLength(200)]
    public string ModelName { get; set; } = string.Empty;

    public string? ApiVersion { get; set; }

    public string? ApiKeyCipherText { get; set; }

    public DateTime? ApiKeyLastUpdatedUtc { get; set; }

    public string? ExtraHeadersJson { get; set; }

    public int? MaxOutputTokens { get; set; }

    public int RequestTimeoutSeconds { get; set; } = 120;

    public bool IsEnabled { get; set; } = true;

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }
}
