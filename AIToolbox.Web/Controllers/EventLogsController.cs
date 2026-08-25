using AIToolbox.Web.Data;
using AIToolbox.Web.Models;
using AIToolbox.Web.Services;
using AIToolbox.Web.ViewModels;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AIToolbox.Web.Controllers;

[Authorize(Policy = "AdminOnly")]
[Route("Admin/EventLogs")]
public sealed class EventLogsController : Controller
{
    private readonly IEventLogRepository _events;
    private readonly IConversationRepository _conversations;
    private readonly IConversationUsageRollupRepository _usageRollups;
    private readonly IRetentionSettingsRepository _retentionSettings;

    public EventLogsController(
        IEventLogRepository events,
        IConversationRepository conversations,
        IConversationUsageRollupRepository usageRollups,
        IRetentionSettingsRepository retentionSettings)
    {
        _events = events;
        _conversations = conversations;
        _usageRollups = usageRollups;
        _retentionSettings = retentionSettings;
    }

    [HttpGet("")]
    public async Task<IActionResult> Index(
        string userType = "all",
        string toolKey = "all",
        string surface = "all",
        string status = "all",
        string? contextKey = null,
        DateOnly? rollupFrom = null,
        DateOnly? rollupTo = null,
        string rollupSurface = "all",
        string tab = "user-logins",
        int page = 1,
        CancellationToken cancellationToken = default)
    {
        var isAiChatTab = string.Equals(tab, "ai-chat", StringComparison.OrdinalIgnoreCase);
        var isAiSubmissionsTab = string.Equals(tab, "ai-submissions", StringComparison.OrdinalIgnoreCase);
        var loginPage = isAiSubmissionsTab || isAiChatTab ? 1 : page;
        var submissionPage = isAiSubmissionsTab ? page : 1;
        var conversationPage = isAiChatTab ? page : 1;

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
        var conversations = await _conversations.GetConversationsAsync(new ConversationListFilter
        {
            Surface = surface,
            Status = status,
            ContextKey = contextKey,
            Page = conversationPage,
            PageSize = 50
        }, cancellationToken);
        var retention = await _retentionSettings.GetAsync(cancellationToken);

        var toDate = rollupTo ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var fromDate = rollupFrom ?? toDate.AddDays(-30);
        var rollupRows = await _usageRollups.GetAsync(fromDate, toDate, rollupSurface, cancellationToken);
        var usageRollupSummary = new UsageRollupSummaryViewModel
        {
            Rows = rollupRows,
            Totals = new ConversationUsageRollup
            {
                Date = fromDate,
                Surface = "Total",
                ConversationCount = rollupRows.Sum(r => r.ConversationCount),
                TurnCount = rollupRows.Sum(r => r.TurnCount),
                TokensIn = rollupRows.Sum(r => r.TokensIn),
                TokensOut = rollupRows.Sum(r => r.TokensOut),
                EstimatedCostUsd = rollupRows.Sum(r => r.EstimatedCostUsd),
                DegradedTierCount = rollupRows.Sum(r => r.DegradedTierCount),
                BudgetExhaustedCount = rollupRows.Sum(r => r.BudgetExhaustedCount),
                FailureCount = rollupRows.Sum(r => r.FailureCount)
            },
            From = fromDate,
            To = toDate,
            Surface = rollupSurface,
            Surfaces = conversations.Surfaces
        };

        return View(new EventLogsViewModel
        {
            UserLogins = logins,
            AiSubmissions = submissions,
            Conversations = conversations,
            RetentionDays = retention.ConversationRetentionDays,
            UsageRollupSummary = usageRollupSummary
        });
    }

    [HttpGet("Conversation/{id:int}")]
    public async Task<IActionResult> Conversation(int id, CancellationToken cancellationToken = default)
    {
        var conversation = await _conversations.GetByIdAsync(id, cancellationToken);
        if (conversation is null)
        {
            return NotFound();
        }

        var messages = await _conversations.GetMessagesAsync(id, cancellationToken);
        var retention = await _retentionSettings.GetAsync(cancellationToken);
        var transcript = messages
            .Select(message => (message, ConversationContentMapper.Map(ConversationMessageCodec.Deserialize(message).Contents)))
            .ToList();

        return View(new ConversationDetailViewModel
        {
            Conversation = conversation,
            Transcript = transcript,
            RetentionDays = retention.ConversationRetentionDays
        });
    }
}
