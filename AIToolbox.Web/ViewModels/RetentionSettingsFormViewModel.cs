using System.ComponentModel.DataAnnotations;

namespace AIToolbox.Web.ViewModels;

public sealed class RetentionSettingsFormViewModel
{
    [Range(1, 3650)]
    [Display(Name = "Conversation retention (days)")]
    public int ConversationRetentionDays { get; set; } = 14;
}
