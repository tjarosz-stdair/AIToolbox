using System.ComponentModel.DataAnnotations;

namespace AIToolbox.Web.ViewModels;

public sealed class AskViewModel
{
    [Required]
    [StringLength(16000, MinimumLength = 3)]
    [Display(Name = "Question")]
    public string Question { get; set; } = string.Empty;

    [Display(Name = "Make this saved query visible to authorized users")]
    public bool IsPublic { get; set; }

    public List<int> SelectedProviderIds { get; set; } = [];

    public List<ProviderCheckboxViewModel> Providers { get; set; } = [];
}
