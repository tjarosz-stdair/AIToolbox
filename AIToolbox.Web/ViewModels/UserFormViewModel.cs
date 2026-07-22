using System.ComponentModel.DataAnnotations;

namespace AIToolbox.Web.ViewModels;

public sealed class UserFormViewModel
{
    public int Id { get; set; }

    [Required]
    [EmailAddress]
    [StringLength(320)]
    public string Email { get; set; } = string.Empty;

    [StringLength(160)]
    [Display(Name = "Display name")]
    public string? DisplayName { get; set; }

    [StringLength(64)]
    [Display(Name = "Tenant ID")]
    public string? TenantId { get; set; }

    [Display(Name = "Admin access")]
    public bool IsAdmin { get; set; }

    [Display(Name = "Enabled")]
    public bool IsEnabled { get; set; } = true;
}

public sealed class TenantConsentUrlsViewModel
{
    public string ClientId { get; set; } = string.Empty;

    public string RedirectUri { get; set; } = string.Empty;

    public string AuthorityTenant { get; set; } = string.Empty;

    public IReadOnlyList<TenantConsentUrlViewModel> Tenants { get; set; } = [];
}

public sealed class TenantConsentUrlViewModel
{
    public string TenantId { get; set; } = string.Empty;

    public IReadOnlyList<string> InferredDomains { get; set; } = [];

    public int AuthorizedUserCount { get; set; }

    public string ConsentUrl { get; set; } = string.Empty;
}
