using AIToolbox.Web.Models;

namespace AIToolbox.Web.ViewModels;

public sealed class EventLogsViewModel
{
    public UserLoginEventResult UserLogins { get; set; } = new();

    public AiSubmissionEventResult AiSubmissions { get; set; } = new();
}
