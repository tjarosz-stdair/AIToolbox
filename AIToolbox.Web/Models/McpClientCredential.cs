using System.ComponentModel.DataAnnotations;

namespace AIToolbox.Web.Models;

public sealed class McpClientCredential
{
    public int Id { get; set; }

    [Required]
    [StringLength(80)]
    public string Surface { get; set; } = string.Empty;

    [Required]
    [StringLength(200)]
    public string ClientId { get; set; } = string.Empty;

    public string? ClientSecretCipherText { get; set; }

    public DateTime? ClientSecretLastUpdatedUtc { get; set; }

    [Required]
    [StringLength(1000)]
    public string TokenEndpointUrl { get; set; } = string.Empty;

    [Required]
    [StringLength(1000)]
    public string McpEndpointUrl { get; set; } = string.Empty;

    [StringLength(500)]
    public string? GrantedScopes { get; set; }

    public bool IsEnabled { get; set; } = true;

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }
}
