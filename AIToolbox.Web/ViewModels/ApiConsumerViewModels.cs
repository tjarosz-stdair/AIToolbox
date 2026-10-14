using System.ComponentModel.DataAnnotations;
using AIToolbox.Web.Models;
using AIToolbox.Web.Security;

namespace AIToolbox.Web.ViewModels;

public sealed class ApiConsumerFormViewModel
{
    public int Id { get; set; }

    [Required]
    [StringLength(120)]
    public string Name { get; set; } = string.Empty;

    [StringLength(1000)]
    public string? Description { get; set; }

    [Display(Name = "Owner email")]
    [StringLength(320)]
    public string? OwnerEmail { get; set; }

    [Display(Name = "Enabled")]
    public bool IsEnabled { get; set; } = true;
}

public sealed class ApiConsumerDetailViewModel
{
    public required ApiConsumer Consumer { get; set; }
    public ApiConsumerKeyFormViewModel NewKey { get; set; } = new();

    /// <summary>Every valid scope + a description of what it grants, for the "add scope"
    /// quick-picker/legend on the new-key form -- KnownApiScopes.All plus "tool:{ToolKey}" for
    /// each registered tool. Free-text entry in the Scopes field is still allowed (a scope for a
    /// tool added after this list was built, e.g.), this just makes the common case
    /// click-to-add-with-an-explanation instead of hand-typed and undocumented.</summary>
    public IReadOnlyList<ScopeDefinition> AvailableScopes { get; set; } = [];
}

public sealed class ApiConsumerKeyFormViewModel
{
    public int ApiConsumerId { get; set; }

    [Required]
    [StringLength(120)]
    public string Name { get; set; } = string.Empty;

    [Display(Name = "Scopes")]
    public string Scopes { get; set; } = "batch:read batch:write batch:cancel tool:OrderTracking";

    [Display(Name = "Expires UTC")]
    public DateTime? ExpiresUtc { get; set; }
}

public sealed class ApiConsumerKeyCreatedViewModel
{
    public required ApiConsumer Consumer { get; set; }
    public required string RawKey { get; set; }
    public required string KeyPrefix { get; set; }
}

public sealed class AdminBatchDetailViewModel
{
    public required AiBatchJob Job { get; set; }
    public IReadOnlyList<AiBatchRequest> Requests { get; set; } = [];
}
