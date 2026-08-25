using System.Text.Json;
using AIToolbox.Web.Data;
using AIToolbox.Web.Models;
using AIToolbox.Web.Security;
using Microsoft.Extensions.AI;
using ModelContextProtocol.Client;

namespace AIToolbox.Web.Services;

/// <summary>
/// Orchestrates one chat turn for the picsearch surface: session-token validation (Part 2),
/// context binding and reset triggers (Part 4.5.5/4.5.6), the MCP tool-calling loop (Part 3 /
/// 4.5.1), and conversation/usage persistence (Part 4.5.3/4.5.8). A valid session token is
/// required — there is no anonymous fallback, per P21 Toolbox's own session-token contract (§6)
/// and to avoid every unauthenticated caller colliding on one shared conversation identity.
/// P21 Toolbox's own budget-exhaustion signal (Part 5.1) is not wired in yet — sequenced as the
/// next slice.
/// </summary>
public sealed class PicSearchChatService : IPicSearchChatService
{
    private const string Surface = "picsearch";
    private const string ToolKey = "PicSearchChat";
    private const int TurnCap = 20;
    private static readonly TimeSpan InactivityWindow = TimeSpan.FromMinutes(30);

    private readonly ISessionTokenValidator _sessionTokens;
    private readonly IConversationRepository _conversations;
    private readonly IConversationUsageRollupRepository _rollups;
    private readonly IPlatformRepository _platform;
    private readonly IAiProviderRepository _providers;
    private readonly IP21McpClientFactory _mcpClientFactory;
    private readonly IAiChatClientFactory _chatClientFactory;
    private readonly IUiCapabilityProvider _uiCapabilities;
    private readonly ILogger<PicSearchChatService> _logger;

    public PicSearchChatService(
        ISessionTokenValidator sessionTokens,
        IConversationRepository conversations,
        IConversationUsageRollupRepository rollups,
        IPlatformRepository platform,
        IAiProviderRepository providers,
        IP21McpClientFactory mcpClientFactory,
        IAiChatClientFactory chatClientFactory,
        IUiCapabilityProvider uiCapabilities,
        ILogger<PicSearchChatService> logger)
    {
        _sessionTokens = sessionTokens;
        _conversations = conversations;
        _rollups = rollups;
        _platform = platform;
        _providers = providers;
        _mcpClientFactory = mcpClientFactory;
        _chatClientFactory = chatClientFactory;
        _uiCapabilities = uiCapabilities;
        _logger = logger;
    }

    public async Task<PicSearchTurnResult> SubmitTurnAsync(
        PicSearchTurnRequest request,
        Func<ToolStepEvent, CancellationToken, Task>? onToolStep = null,
        CancellationToken cancellationToken = default)
    {
        var tokenResult = await _sessionTokens.ValidateAsync(request.SessionToken, cancellationToken);

        // Anonymous access is removed, not degraded-and-allowed: P21 Toolbox's own session-token
        // contract (§6) says anonymous AI access isn't supported in v1 "by construction," and
        // allowing it here meant every anonymous caller collided on one "anonymous" identity,
        // sharing a single conversation row (and its transcript) per context key.
        if (!tokenResult.Present)
        {
            return Failure(
                PicSearchErrorCode.SessionTokenRequired,
                "A valid P21 Toolbox session token is required to use this chat.",
                sessionTokenPresent: false,
                sessionTokenValid: false);
        }

        if (!tokenResult.IsValid)
        {
            // Present but INVALID is the fabricated-token spend-abuse scenario (Part 2.1) —
            // reject outright, before any AI-provider-spending work happens.
            _logger.LogWarning("Rejected picsearch chat turn: session token present but invalid ({Reason}).", tokenResult.RejectReason);
            return Failure(
                PicSearchErrorCode.SessionTokenRejected,
                $"Session token rejected ({tokenResult.RejectReason}).",
                sessionTokenPresent: true,
                sessionTokenValid: false);
        }

        var sub = tokenResult.Sub!;
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // Admin-controlled availability + provider selection for this surface, reusing the
        // existing ToolDefinitions/Providers admin mechanism rather than inventing a new one.
        // Note: ToolUserAllowList doesn't apply here — picsearch's end users are P21 Toolbox's
        // own customers' staff, identified by session-token sub, not AIToolbox-provisioned
        // accounts. Access control for this surface is the OAuth scope + session token pair.
        var tool = await _platform.GetToolAsync(ToolKey, cancellationToken);
        if (tool is null || !tool.IsEnabled || tool.ActiveProviderId is null)
        {
            return Failure(
                PicSearchErrorCode.NotConfigured,
                "The chat assistant isn't configured yet. An admin needs to set an active AI provider for 'PicSearchChat' in Admin > Tools.",
                sessionTokenPresent: true,
                sessionTokenValid: true);
        }

        var provider = await _providers.GetByIdAsync(tool.ActiveProviderId.Value, cancellationToken);
        if (provider is null || !provider.IsEnabled)
        {
            return Failure(
                PicSearchErrorCode.NotConfigured,
                "The AI provider configured for the chat assistant is missing or disabled.",
                sessionTokenPresent: true,
                sessionTokenValid: true);
        }

        string? resetMessage = null;

        // Context binding (Part 4.5.5): a conversation may never span two context keys. Close
        // any stale conversation bound to a different context rather than leaving it orphaned.
        var existingForSurface = await _conversations.GetActiveConversationForSurfaceAsync(sub, Surface, cancellationToken);
        if (existingForSurface is not null && !string.Equals(existingForSurface.ContextKey, request.ContextKey, StringComparison.Ordinal))
        {
            await _conversations.CloseConversationAsync(existingForSurface.Id, ConversationStatus.ClosedContextChange, cancellationToken);
            resetMessage = $"Starting a new conversation for '{request.ContextKey}'.";
        }

        var conversation = await _conversations.GetActiveConversationAsync(sub, Surface, request.ContextKey, cancellationToken);
        if (conversation is null)
        {
            conversation = await CreateConversationAsync(sub, request.ContextKey, today, cancellationToken);
        }
        else if (conversation.UserTurnCount >= TurnCap)
        {
            await _conversations.CloseConversationAsync(conversation.Id, ConversationStatus.ClosedTurnCap, cancellationToken);
            resetMessage = "This conversation reached its turn limit. Starting a new conversation.";
            conversation = await CreateConversationAsync(sub, request.ContextKey, today, cancellationToken);
        }
        else if (DateTime.UtcNow - conversation.LastActiveUtc > InactivityWindow)
        {
            await _conversations.CloseConversationAsync(conversation.Id, ConversationStatus.ClosedInactivity, cancellationToken);
            resetMessage = "This conversation timed out from inactivity. Starting a new conversation.";
            conversation = await CreateConversationAsync(sub, request.ContextKey, today, cancellationToken);
        }

        var storedMessages = await _conversations.GetMessagesAsync(conversation.Id, cancellationToken);
        var history = storedMessages.Select(ConversationMessageCodec.Deserialize).ToList();
        var nextSequence = storedMessages.Count == 0 ? 0 : storedMessages[^1].Sequence + 1;

        var userMessage = new ChatMessage(ChatRole.User, request.UserMessage);
        await _conversations.AppendMessageAsync(ConversationMessageCodec.Serialize(conversation.Id, userMessage, nextSequence++, null, null), cancellationToken);

        McpClient? mcpClient = null;
        ChatResponse response;
        var uiToolsByName = new Dictionary<string, UiIntentFunction>(StringComparer.Ordinal);
        try
        {
            mcpClient = await _mcpClientFactory.ConnectAsync(Surface, request.SessionToken, cancellationToken);
            var mcpTools = await _mcpClientFactory.GetToolsAsync(mcpClient, conversation.Id.ToString(), cancellationToken);
            var uiTools = await _uiCapabilities.GetToolsAsync(Surface, request.UiSurfaces, cancellationToken);
            uiToolsByName = uiTools
                .OfType<UiIntentFunction>()
                .ToDictionary(tool => tool.Name, StringComparer.Ordinal);

            var tools = new List<AITool>(mcpTools.Cast<AITool>());
            tools.AddRange(uiTools);

            var chatClient = _chatClientFactory.Create(provider, onToolStep);
            var chatOptions = new ChatOptions
            {
                Instructions = BuildSystemPrompt(request.ContextKey, request.UiSurfaces, request.AutoNavigate),
                Tools = tools
            };

            var turnMessages = history.Append(userMessage).ToList();
            response = await chatClient.GetResponseAsync(turnMessages, chatOptions, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PicSearch chat turn failed for conversation {ConversationId}.", conversation.Id);
            await _rollups.IncrementAsync(today, Surface, failureDelta: 1, cancellationToken: cancellationToken);
            return Failure(
                PicSearchErrorCode.TurnFailed,
                "The assistant couldn't process that question right now. Please try again.",
                sessionTokenPresent: true,
                sessionTokenValid: true,
                conversation.Id,
                resetMessage);
        }
        finally
        {
            if (mcpClient is not null)
            {
                await mcpClient.DisposeAsync();
            }
        }

        LogTieredToolResults(conversation.Id, tokenResult.Present && tokenResult.IsValid, response.Messages);

        foreach (var message in response.Messages)
        {
            await _conversations.AppendMessageAsync(
                ConversationMessageCodec.Serialize(conversation.Id, message, nextSequence++, response.ModelId, provider.Id),
                cancellationToken);
        }

        var tokensIn = response.Usage?.InputTokenCount is { } inputTokens ? (int)inputTokens : 0;
        var tokensOut = response.Usage?.OutputTokenCount is { } outputTokens ? (int)outputTokens : 0;
        await _conversations.RecordTurnAsync(conversation.Id, tokensIn, tokensOut, cancellationToken);

        var estimatedCost = await _platform.EstimateCostAsync(provider, tokensIn, tokensOut, cancellationToken) ?? 0m;
        await _rollups.IncrementAsync(
            today,
            Surface,
            turnDelta: 1,
            tokensInDelta: tokensIn,
            tokensOutDelta: tokensOut,
            estimatedCostUsdDelta: estimatedCost,
            cancellationToken: cancellationToken);

        var toolsUsed = response.Messages
            .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
            .Select(call => call.Name)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var intents = ExtractQueuedUiIntents(response.Messages, uiToolsByName);

        return new PicSearchTurnResult(
            conversation.Id,
            resetMessage is not null,
            resetMessage,
            response.Text,
            toolsUsed,
            intents,
            SessionTokenPresent: true,
            SessionTokenValid: true,
            Code: null,
            ErrorMessage: null);
    }

    /// <summary>
    /// Manual reset (Part 4.5.6): closes the active conversation for this sub+surface+context with
    /// ClosedManual, same retention-countdown/purge treatment as any other reset trigger. No prior
    /// caller ever triggered this — a direct browser session with no P21 broker driving context
    /// changes never trips the automatic triggers (context change, turn cap, inactivity), so this
    /// was the one reset path with no way to invoke it until now.
    /// </summary>
    public async Task<PicSearchResetResult> ResetConversationAsync(string? sessionToken, string contextKey, CancellationToken cancellationToken = default)
    {
        var tokenResult = await _sessionTokens.ValidateAsync(sessionToken, cancellationToken);

        if (!tokenResult.Present)
        {
            return new PicSearchResetResult(false, PicSearchErrorCode.SessionTokenRequired, "A valid P21 Toolbox session token is required to use this chat.");
        }

        if (!tokenResult.IsValid)
        {
            _logger.LogWarning("Rejected picsearch chat reset: session token present but invalid ({Reason}).", tokenResult.RejectReason);
            return new PicSearchResetResult(false, PicSearchErrorCode.SessionTokenRejected, $"Session token rejected ({tokenResult.RejectReason}).");
        }

        var conversation = await _conversations.GetActiveConversationAsync(tokenResult.Sub!, Surface, contextKey, cancellationToken);
        if (conversation is not null)
        {
            await _conversations.CloseConversationAsync(conversation.Id, ConversationStatus.ClosedManual, cancellationToken);
        }

        return new PicSearchResetResult(true, null, null);
    }

    /// <summary>
    /// Lets the client learn, on load, whether there's already an Active conversation for this
    /// sub+surface+contextKey (SubmitTurnAsync already silently resumes one server-side — this is
    /// what finally lets the browser reflect that instead of always starting from a blank
    /// transcript). Reduces each stored message down to just its displayable text; a message that
    /// carried only tool-call/tool-result content (no TextContent) is dropped rather than
    /// returned as an empty turn — the live chat UI never rendered those directly anyway, only the
    /// admin transcript view does.
    /// </summary>
    public async Task<PicSearchHistoryResult> GetActiveConversationHistoryAsync(string? sessionToken, string contextKey, CancellationToken cancellationToken = default)
    {
        var tokenResult = await _sessionTokens.ValidateAsync(sessionToken, cancellationToken);

        if (!tokenResult.Present)
        {
            return new PicSearchHistoryResult(false, PicSearchErrorCode.SessionTokenRequired, "A valid P21 Toolbox session token is required to use this chat.", null, []);
        }

        if (!tokenResult.IsValid)
        {
            _logger.LogWarning("Rejected picsearch history request: session token present but invalid ({Reason}).", tokenResult.RejectReason);
            return new PicSearchHistoryResult(false, PicSearchErrorCode.SessionTokenRejected, $"Session token rejected ({tokenResult.RejectReason}).", null, []);
        }

        var conversation = await _conversations.GetActiveConversationAsync(tokenResult.Sub!, Surface, contextKey, cancellationToken);
        if (conversation is null)
        {
            return new PicSearchHistoryResult(true, null, null, null, []);
        }

        var storedMessages = await _conversations.GetMessagesAsync(conversation.Id, cancellationToken);
        var turns = new List<PicSearchHistoryTurn>();
        foreach (var stored in storedMessages)
        {
            var text = ConversationMessageCodec.Deserialize(stored).Text;
            if (!string.IsNullOrWhiteSpace(text))
            {
                turns.Add(new PicSearchHistoryTurn(stored.Role, text));
            }
        }

        return new PicSearchHistoryResult(true, null, null, conversation, turns);
    }

    /// <summary>
    /// A ui_ call the 2-per-turn cap rejected (AiChatClientFactory's FunctionInvoker hook) comes
    /// back with a {"status":"rejected",...} result, not UiIntentFunction's own
    /// {"status":"queued"} — only the latter should ever reach the browser as a real intent.
    /// Public and static (not private) specifically so this can be unit tested without the rest
    /// of PicSearchChatService's DI graph — this path has never run against anything real, since
    /// nothing consumed it before now.
    /// </summary>
    public static IReadOnlyList<UiIntentDescriptor> ExtractQueuedUiIntents(
        IList<ChatMessage> messages,
        IReadOnlyDictionary<string, UiIntentFunction> uiToolsByName)
    {
        if (uiToolsByName.Count == 0)
        {
            return [];
        }

        var uiCallsById = messages
            .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
            .Where(call => uiToolsByName.ContainsKey(call.Name))
            .ToDictionary(call => call.CallId, call => call);

        if (uiCallsById.Count == 0)
        {
            return [];
        }

        var results = new List<UiIntentDescriptor>();
        foreach (var message in messages)
        {
            foreach (var result in message.Contents.OfType<FunctionResultContent>())
            {
                if (!uiCallsById.TryGetValue(result.CallId, out var call))
                {
                    continue;
                }

                if (result.Result is not IDictionary<string, object?> statusResult
                    || !statusResult.TryGetValue("status", out var status)
                    || status as string != "queued")
                {
                    continue;
                }

                var tool = uiToolsByName[call.Name];
                results.Add(new UiIntentDescriptor(call.Name, tool.Kind, tool.UiSurface, call.Arguments ?? new Dictionary<string, object?>()));
            }
        }

        return results;
    }

    private async Task<Conversation> CreateConversationAsync(string sub, string contextKey, DateOnly today, CancellationToken cancellationToken)
    {
        var id = await _conversations.CreateConversationAsync(
            new Conversation { Sub = sub, Surface = Surface, ContextKey = contextKey },
            cancellationToken);
        await _rollups.IncrementAsync(today, Surface, conversationDelta: 1, cancellationToken: cancellationToken);
        return await _conversations.GetByIdAsync(id, cancellationToken)
            ?? throw new InvalidOperationException("The newly created conversation could not be reloaded.");
    }

    // Part 5.3: get_item_availability/get_warranty_by_serial are gated solely by the session
    // token, not OAuth scope — an origin/aud misconfiguration degrades every response for every
    // user, silently, with no error anywhere except P21 Toolbox's own audit table. Logging every
    // such result's raw content here at least makes it diagnosable from this side too.
    private static readonly HashSet<string> TieredToolNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "get_item_availability",
        "get_warranty_by_serial"
    };

    private void LogTieredToolResults(int conversationId, bool sessionTokenValid, IList<ChatMessage> messages)
    {
        var callNamesById = messages
            .SelectMany(m => m.Contents.OfType<FunctionCallContent>())
            .Where(call => TieredToolNames.Contains(call.Name))
            .ToDictionary(call => call.CallId, call => call.Name);

        if (callNamesById.Count == 0)
        {
            return;
        }

        foreach (var result in messages.SelectMany(m => m.Contents.OfType<FunctionResultContent>()))
        {
            if (!callNamesById.TryGetValue(result.CallId, out var toolName))
            {
                continue;
            }

            var resultText = result.Result?.ToString() ?? string.Empty;
            _logger.LogInformation(
                "Tiered tool {ToolName} result for conversation {ConversationId} (SessionTokenValid={SessionTokenValid}): {ResultExcerpt}",
                toolName,
                conversationId,
                sessionTokenValid,
                resultText.Length > 500 ? resultText[..500] : resultText);

            // Best-effort heuristic pending confirmation of get_item_availability/
            // get_warranty_by_serial's exact response schema from the P21 Toolbox side — the
            // integration handoff doc refers to the privileged tier literally as "Numeric".
            // Adjust this check once that schema is confirmed; don't treat it as authoritative.
            var looksDegraded = !resultText.Contains("Numeric", StringComparison.OrdinalIgnoreCase);
            if (sessionTokenValid && looksDegraded)
            {
                _logger.LogWarning(
                    "Possible silent tier degradation: {ToolName} returned a non-numeric-looking result for conversation " +
                    "{ConversationId} despite a valid session token being attached. This combination is almost always an " +
                    "aud/origin misconfiguration between this instance and P21 Toolbox's dbo.tenant.ai_toolbox_origin_url, " +
                    "not a legitimate anonymous request — see Part 5.3.",
                    toolName,
                    conversationId);
            }
        }
    }

    // Directive §5.6: surface-set changes never reset the conversation, but the model still
    // needs to know what's currently open so it doesn't offer to navigate a screen that just
    // closed. Restating the current set each turn (rather than diffing against a prior turn)
    // is the deliberately simple version — Conversation/ConversationMessage carry no persisted
    // "last known surfaces" field, so there is nothing to diff against across requests, and
    // restating achieves the same practical effect: a closed surface simply stops being mentioned.
    private static string BuildSystemPrompt(string contextKey, IReadOnlyList<string> uiSurfaces, bool autoNavigate)
    {
        var surfaceLine = uiSurfaces.Count == 0
            ? "No additional screens are currently open — you can only answer from data, not navigate anywhere."
            : $"Currently open screens available for navigation: {string.Join(", ", uiSurfaces)}.";

        // "Auto-navigate PIC Search" checkbox (checked by default) — the user's explicit call is
        // that asking permission first ("do you want me to show you in the BOM?") is unnecessary
        // friction; navigating proactively once the answer is known is standard practice, not an
        // exception. First wording (soft "don't ask, it's standard practice") was live-tested and
        // didn't hold: the model skipped navigating on any turn where it answered from earlier
        // conversation context rather than a fresh tool call this turn, and still asked permission
        // instead of just navigating. This version is deliberately a hard MUST/forbidden-phrasing
        // rule with a worked example, not a preference, and explicitly covers the from-memory case.
        var navigationLine = autoNavigate
            ? """
              Auto-navigate is ON. This is a hard rule, not a preference:
              - Whenever your answer names a specific part, model, or BOM that a navigation tool covers, you MUST call
                that navigation tool as part of the SAME response — every time, with no exceptions.
              - This applies even when the answer came from earlier in this conversation rather than a tool call you
                just made this turn. "I already told them this" is not a reason to skip navigating.
              - You are FORBIDDEN from asking a yes/no question about whether to navigate or show something — no
                "want me to show it in the BOM?", no "should I pull that up?", no "let me know if you'd like to see
                it". Asking that question is exactly the behavior this setting exists to eliminate.
              - Correct pattern: state the answer, then call the navigation tool in the same turn, with no question
                mark about it anywhere in your reply.
              - Incorrect pattern: state the answer, then ask the user whether they want it shown. Never do this
                while auto-navigate is ON.
              - The only valid reason to not navigate is that no navigation tool actually matches what you described.
              """
            : "Auto-navigate is OFF: don't call a navigation tool unless the user has asked to be shown something. If " +
              "showing them would help, ask first rather than navigating unprompted.";

        return $"""
            You are a P21 product and parts assistant embedded in PIC Search.
            This conversation is bound to context '{contextKey}' — a specific order or customer.
            Only answer questions about this context. Always explicitly name the order or customer your answer refers to.
            If the user asks about a different order or customer than '{contextKey}', tell them you can't do that in this
            conversation and that they should start a new one for that context instead of guessing or mixing data.

            You answer using data tools (MCP tools) first. Navigation (ui_-prefixed tools) is presentational only — it shows
            the user something on their screen, it never returns product data. Never call a navigation tool expecting it to
            answer a question; resolve the answer first (from a data tool call this turn, or from earlier in the
            conversation), then navigate per the auto-navigate rule below.
            {surfaceLine}
            {navigationLine}
            """;
    }

    private static PicSearchTurnResult Failure(
        string code,
        string errorMessage,
        bool sessionTokenPresent,
        bool sessionTokenValid,
        int conversationId = 0,
        string? resetMessage = null)
    {
        return new PicSearchTurnResult(
            conversationId,
            resetMessage is not null,
            resetMessage,
            string.Empty,
            [],
            [],
            sessionTokenPresent,
            sessionTokenValid,
            code,
            errorMessage);
    }
}
