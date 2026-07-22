using System.ComponentModel.DataAnnotations;

namespace AIToolbox.Web.Models;

public sealed class AuthorizedUser
{
    public int Id { get; set; }

    [Required]
    [EmailAddress]
    [StringLength(320)]
    public string Email { get; set; } = string.Empty;

    public string EmailNormalized => Email.Trim().ToUpperInvariant();

    [StringLength(160)]
    public string? DisplayName { get; set; }

    [StringLength(64)]
    public string? TenantId { get; set; }

    public bool IsAdmin { get; set; }

    public bool IsEnabled { get; set; } = true;

    public DateTime CreatedUtc { get; set; }

    public DateTime UpdatedUtc { get; set; }

    public DateTime? LastLoginUtc { get; set; }
}
