using System.ComponentModel.DataAnnotations;

namespace AIToolbox.Web.ViewModels;

public sealed class PicSearchResetRequest
{
    [Required]
    [StringLength(200)]
    public string ContextKey { get; set; } = string.Empty;
}
