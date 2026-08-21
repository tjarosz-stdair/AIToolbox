using System.ComponentModel.DataAnnotations;

namespace AIToolbox.Web.ViewModels;

public sealed class McpClientCredentialFormViewModel
{
    public int Id { get; set; }

    [Required]
    [StringLength(80)]
    [Display(Name = "Surface")]
    public string Surface { get; set; } = string.Empty;

    [Required]
    [StringLength(200)]
    [Display(Name = "Client ID")]
    public string ClientId { get; set; } = string.Empty;

    [DataType(DataType.Password)]
    [Display(Name = "Client secret")]
    public string? ClientSecret { get; set; }

    [Display(Name = "Clear stored secret")]
    public bool ClearClientSecret { get; set; }

    [Required]
    [StringLength(1000)]
    [Url]
    [Display(Name = "Token endpoint URL")]
    public string TokenEndpointUrl { get; set; } = string.Empty;

    [Required]
    [StringLength(1000)]
    [Url]
    [Display(Name = "MCP endpoint URL")]
    public string McpEndpointUrl { get; set; } = string.Empty;

    [StringLength(500)]
    [Display(Name = "Granted scopes")]
    public string? GrantedScopes { get; set; }

    [Display(Name = "Enabled")]
    public bool IsEnabled { get; set; } = true;

    public bool HasStoredClientSecret { get; set; }
}
