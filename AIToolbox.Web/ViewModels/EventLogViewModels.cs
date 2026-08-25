using AIToolbox.Web.Models;

namespace AIToolbox.Web.ViewModels;

public sealed class EventLogsViewModel
{
    public UserLoginEventResult UserLogins { get; set; } = new();

    public AiSubmissionEventResult AiSubmissions { get; set; } = new();

    public ConversationListResult Conversations { get; set; } = new();

    public int RetentionDays { get; set; }

    public UsageRollupSummaryViewModel? UsageRollupSummary { get; set; }
}
