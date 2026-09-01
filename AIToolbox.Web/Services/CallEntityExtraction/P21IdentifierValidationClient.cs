using System.Text.Json;
using System.Text.Json.Nodes;
using AIToolbox.Web.Models;
using Microsoft.Extensions.Caching.Memory;
using ModelContextProtocol;
using static AIToolbox.Web.Services.CallEntityExtraction.P21ValidationResultMapper;

namespace AIToolbox.Web.Services.CallEntityExtraction;

/// <summary>
/// Calls P21 Toolbox's live MCP facade (POST /api/mcp) directly and deterministically -- not
/// through the LLM tool-calling loop PicSearchChatService uses. Result parsing lives in
/// P21ValidationResultMapper; this class owns the network call, caching, and circuit breaker.
///
/// Requires an McpClientCredential row provisioned (Admin &gt; MCP Client Credentials) for the
/// "call-entity-extraction" surface, separate from PicSearchChatService's "picsearch" credential
/// so this background pipeline's call volume/budget/audit trail is independently scoped.
///
/// Modeled on UiCapabilityProvider's shape: never throws, degrades to null on any failure, and a
/// short in-memory circuit breaker skips calls entirely after a run of failures rather than
/// hammering an unreachable P21 Toolbox once per candidate.
/// </summary>
public sealed class P21IdentifierValidationClient : IP21IdentifierValidationClient
{
    public const string Surface = "call-entity-extraction";

    private static readonly TimeSpan CacheTtl = TimeSpan.FromMinutes(12);
    private const int CircuitBreakerFailureThreshold = 5;
    private static readonly TimeSpan CircuitBreakerCooldown = TimeSpan.FromSeconds(60);

    private readonly IP21McpClientFactory _mcpClientFactory;
    private readonly IMemoryCache _cache;
    private readonly ILogger<P21IdentifierValidationClient> _logger;

    private readonly object _circuitGate = new();
    private int _consecutiveFailures;
    private DateTime? _circuitOpenUntilUtc;

    public P21IdentifierValidationClient(
        IP21McpClientFactory mcpClientFactory,
        IMemoryCache cache,
        ILogger<P21IdentifierValidationClient> logger)
    {
        _mcpClientFactory = mcpClientFactory;
        _cache = cache;
        _logger = logger;
    }

    public async Task<ValidationResult?> ValidateAsync(IdentifierCandidate candidate, string callId, CancellationToken cancellationToken = default)
    {
        var toolName = candidate.TypeGuess == IdentifierTypeGuess.Serial ? GetWarrantyBySerialTool : ResolveModelTool;
        var cacheKey = $"{toolName}:{candidate.CanonicalValue}";

        if (_cache.TryGetValue(cacheKey, out ValidationResult? cached))
        {
            return cached;
        }

        if (IsCircuitOpen())
        {
            _logger.LogDebug(
                "Skipping P21 validation for {CanonicalValue} -- circuit breaker open until {OpenUntilUtc}.",
                candidate.CanonicalValue,
                _circuitOpenUntilUtc);
            return null;
        }

        try
        {
            var result = await CallP21Async(toolName, candidate, callId, cancellationToken);
            RecordSuccess();

            // Only cache a real outcome -- null (suppressed/not-found/unreachable) shouldn't stick
            // around and block a legitimate re-mention later in the call from trying again.
            if (result is not null)
            {
                _cache.Set(cacheKey, result, CacheTtl);
            }

            return result;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            RecordFailure();
            _logger.LogWarning(ex, "P21 validation call failed for {CanonicalValue} via {ToolName}.", candidate.CanonicalValue, toolName);
            return null;
        }
    }

    private async Task<ValidationResult?> CallP21Async(string toolName, IdentifierCandidate candidate, string callId, CancellationToken cancellationToken)
    {
        // Never pass a caller session token -- this is an unattended background pipeline, not a
        // verified user, so P21 Toolbox correctly degrades PII-bearing warranty fields (owner,
        // installation address) to their non-PII "Summary" tier. That's the desired behavior here,
        // not a limitation to work around.
        await using var client = await _mcpClientFactory.ConnectAsync(Surface, sessionToken: null, cancellationToken);

        var arguments = toolName == GetWarrantyBySerialTool
            ? new Dictionary<string, object?> { ["serialNumber"] = candidate.CanonicalValue }
            : new Dictionary<string, object?> { ["query"] = candidate.CanonicalValue };

        var requestOptions = new RequestOptions { Meta = new JsonObject { ["sessionId"] = callId } };
        var result = await client.CallToolAsync(toolName, arguments, progress: null, requestOptions, cancellationToken);

        if (result.IsError == true || result.StructuredContent is not { } structured || structured.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return toolName == GetWarrantyBySerialTool
            ? MapWarrantyResult(structured, candidate.CanonicalValue)
            : MapModelResolution(structured, candidate.CanonicalValue);
    }

    private bool IsCircuitOpen()
    {
        lock (_circuitGate)
        {
            if (_circuitOpenUntilUtc is null)
            {
                return false;
            }

            if (DateTime.UtcNow < _circuitOpenUntilUtc)
            {
                return true;
            }

            // Cooldown elapsed -- let the next call through as a probe; RecordSuccess/RecordFailure
            // decide whether the circuit re-closes or re-opens.
            _circuitOpenUntilUtc = null;
            _consecutiveFailures = 0;
            return false;
        }
    }

    private void RecordSuccess()
    {
        lock (_circuitGate)
        {
            _consecutiveFailures = 0;
            _circuitOpenUntilUtc = null;
        }
    }

    private void RecordFailure()
    {
        lock (_circuitGate)
        {
            _consecutiveFailures++;
            if (_consecutiveFailures >= CircuitBreakerFailureThreshold)
            {
                _circuitOpenUntilUtc = DateTime.UtcNow + CircuitBreakerCooldown;
            }
        }
    }
}
