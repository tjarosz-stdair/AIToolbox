using System.ComponentModel.DataAnnotations;

namespace AIToolbox.Web.Models;

/// <summary>
/// A configured non-chat, non-MCP bolt-on service integration (e.g. Azure Speech-to-Text) —
/// deliberately separate from AiProvider (chat-completion providers, tool/provider-assignment
/// shaped) and McpClientCredential (MCP OAuth secrets). ConfigJson is a schema-less bag
/// (e.g. {"region":"eastus","language":"en-US"} for AzureSpeech) so a future resource type with
/// entirely different settings never needs its own migration.
/// </summary>
public sealed class ExternalResource
{
    public int Id { get; set; }

    [Required]
    [StringLength(80)]
    public string ResourceKey { get; set; } = string.Empty;

    public ExternalResourceType ResourceType { get; set; } = ExternalResourceType.AzureSpeech;

    [Required]
    [StringLength(150)]
    public string DisplayName { get; set; } = string.Empty;

    public string? ConfigJson { get; set; }

    public string? SecretCipherText { get; set; }

    public DateTime? SecretLastUpdatedUtc { get; set; }

    public bool IsEnabled { get; set; } = true;

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }
}
