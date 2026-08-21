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
/// 4.5.1), and conversation/usage persistence (Part 4.5.3/4.5.8). Degraded-tier detection and
/// P21 Toolbox's own budget-exhaustion signal (Part 5) are not wired in yet — sequenced as the
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
    private readonly ILogger<PicSearchChatService> _logger;

    public PicSearchChatService(
        ISessionTokenValidator sessionTokens,
        IConversationRepository conversations,
        IConversationUsageRollupRepository rollups,
        IPlatformRepository platform,
        IAiProviderRepository providers,
        IP21McpClientFactory mcpClientFactory,
        IAiChatClientFactory chatClientFactory,
        ILogger<PicSearchChatService> logger)
    {
        _sessionTokens = sessionTokens;
        _conversations = conversations;
        _rollups = rollups;
        _platform = platform;
        _providers = providers;
        _mcpClientFactory = mcpClientFactory;
        _chatClientFactory = chatClientFactory;
        _logger = logger;
    }

    public async Task<PicSearchTurnResult> SubmitTurnAsync(PicSearchTurnRequest request, CancellationToken cancellationToken = default)
    {
        var tokenResult = await _sessionTokens.ValidateAsync(request.SessionToken, cancellationToken);
        if (tokenResult.Present && !tokenResult.IsValid)
        {
            // A PRESENT but INVALID token is the fabricated-token spend-abuse scenario (Part
            // 2.1) — reject outright, before any AI-provider-spending work happens.
            _logger.LogWarning("Rejected picsearch chat turn: session token present but invalid ({Reason}).", tokenResult.RejectReason);
            return Failure(null, false, $"Session token rejected ({tokenResult.RejectReason}).");
        }

        var sub = tokenResult.IsValid ? tokenResult.Sub! : "anonymous";
        var today = DateOnly.FromDateTime(DateTime.UtcNow);

        // Admin-controlled availability + provider selection for this surface, reusing the
        // existing ToolDefinitions/Providers admin mechanism rather than inventing a new one.
        // Note: ToolUserAllowList doesn't apply here — picsearch's end users are P21 Toolbox's
        // own customers' staff, identified by session-token sub, not AIToolbox-provisioned
        // accounts. Access control for this surface is the OAuth scope + session token pair.
        var tool = await _platform.GetToolAsync(ToolKey, cancellationToken);
        if (tool is null || !tool.IsEnabled || tool.ActiveProviderId is null)
        {
            return Failure(sub, tokenResult.IsValid, "The chat assistant isn't configured yet. An admin needs to set an active AI provider for 'PicSearchChat' in Admin > Tools.");
        }

        var provider = await _providers.GetByIdAsync(tool.ActiveProviderId.Value, cancellationToken);
        if (provider is null || !provider.IsEnabled)
        {
            return Failure(sub, tokenResult.IsValid, "The AI provider configured for the chat assistant is missing or disabled.");
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
        var history = storedMessages.Select(DeserializeMessage).ToList();
        var nextSequence = storedMessages.Count == 0 ? 0 : storedMessages[^1].Sequence + 1;

        var userMessage = new ChatMessage(ChatRole.User, request.UserMessage);
        await _conversations.AppendMessageAsync(SerializeMessage(conversation.Id, userMessage, nextSequence++, null, null), cancellationToken);

        McpClient? mcpClient = null;
        ChatResponse response;
        try
        {
            mcpClient = await _mcpClientFactory.ConnectAsync(Surface, tokenResult.IsValid ? request.SessionToken : null, cancellationToken);
            var mcpTools = await _mcpClientFactory.GetToolsAsync(mcpClient, conversation.Id.ToString(), cancellationToken);

            var chatClient = _chatClientFactory.Create(provider);
            var chatOptions = new ChatOptions
            {
                Instructions = BuildSystemPrompt(request.ContextKey),
                Tools = mcpTools.Cast<AITool>().ToList()
            };

            var turnMessages = history.Append(userMessage).ToList();
            response = await chatClient.GetResponseAsync(turnMessages, chatOptions, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "PicSearch chat turn failed for conversation {ConversationId}.", conversation.Id);
            await _rollups.IncrementAsync(today, Surface, failureDelta: 1, cancellationToken: cancellationToken);
            return Failure(sub, tokenResult.IsValid, "The assistant couldn't process that question right now. Please try again.", conversation.Id, resetMessage);
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
                SerializeMessage(conversation.Id, message, nextSequence++, response.ModelId, provider.Id),
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

        return new PicSearchTurnResult(
            conversation.Id,
            resetMessage is not null,
            resetMessage,
            response.Text,
            tokenResult.Present,
            tokenResult.IsValid,
            null);
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

    private static string BuildSystemPrompt(string contextKey)
    {
        return $"""
            You are a P21 product and parts assistant embedded in PIC Search.
            This conversation is bound to context '{contextKey}' — a specific order or customer.
            Only answer questions about this context. Always explicitly name the order or customer your answer refers to.
            If the user asks about a different order or customer than '{contextKey}', tell them you can't do that in this
            conversation and that they should start a new one for that context instead of guessing or mixing data.
            """;
    }

    private static ConversationMessage SerializeMessage(int conversationId, ChatMessage message, int sequence, string? modelId, int? aiProviderId)
    {
        return new ConversationMessage
        {
            ConversationId = conversationId,
            Role = message.Role.Value,
            Sequence = sequence,
            ContentJson = JsonSerializer.Serialize(message.Contents, AIJsonUtilities.DefaultOptions),
            ModelId = modelId,
            AiProviderId = aiProviderId
        };
    }

    private static ChatMessage DeserializeMessage(ConversationMessage stored)
    {
        var contents = JsonSerializer.Deserialize<List<AIContent>>(stored.ContentJson, AIJsonUtilities.DefaultOptions) ?? [];
        return new ChatMessage(new ChatRole(stored.Role), contents);
    }

    private static PicSearchTurnResult Failure(string? sub, bool sessionTokenValid, string errorMessage, int conversationId = 0, string? resetMessage = null)
    {
        return new PicSearchTurnResult(conversationId, resetMessage is not null, resetMessage, string.Empty, sub is not null, sessionTokenValid, errorMessage);
    }
}
