using System.ComponentModel.DataAnnotations;

namespace AIToolbox.Web.ViewModels;

public sealed class PicSearchAskRequest
{
    [Required]
    [StringLength(200)]
    public string ContextKey { get; set; } = string.Empty;

    [Required]
    [StringLength(4000)]
    public string Message { get; set; } = string.Empty;
}
