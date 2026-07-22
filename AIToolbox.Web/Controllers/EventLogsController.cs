using AIToolbox.Web.Data;
using AIToolbox.Web.Models;
using AIToolbox.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AIToolbox.Web.Controllers;

[Authorize(Policy = "AdminOnly")]
[Route("Admin/EventLogs")]
public sealed class EventLogsController : Controller
{
    private readonly IEventLogRepository _events;

    public EventLogsController(IEventLogRepository events)
    {
        _events = events;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(
        string userType = "all",
        string toolKey = "all",
        string tab = "user-logins",
        int page = 1,
        CancellationToken cancellationToken = default)
    {
        var loginPage = string.Equals(tab, "ai-submissions", StringComparison.OrdinalIgnoreCase) ? 1 : page;
        var submissionPage = string.Equals(tab, "ai-submissions", StringComparison.OrdinalIgnoreCase) ? page : 1;
        var logins = await _events.GetUserLoginEventsAsync(new UserLoginEventFilter
        {
            UserType = userType,
            Page = loginPage,
            PageSize = 50
        }, cancellationToken);
        var submissions = await _events.GetAiSubmissionEventsAsync(new AiSubmissionEventFilter
        {
            ToolKey = toolKey,
            Page = submissionPage,
            PageSize = 50
        }, cancellationToken);

        return View(new EventLogsViewModel { UserLogins = logins, AiSubmissions = submissions });
    }
}
