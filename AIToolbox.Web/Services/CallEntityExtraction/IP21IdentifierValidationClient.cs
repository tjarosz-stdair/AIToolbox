using AIToolbox.Web.Models;

namespace AIToolbox.Web.Services.CallEntityExtraction;

public interface IP21IdentifierValidationClient
{
    /// <summary>
    /// Validates one candidate against P21 Toolbox's MCP facade. Returns null when the candidate
    /// should NOT produce a notification -- P21 said it's genuinely not found (model/item
    /// candidates), P21 is unreachable, or the call itself failed. Never throws.
    /// </summary>
    Task<ValidationResult?> ValidateAsync(IdentifierCandidate candidate, string callId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Side-effect-free snapshot of whether the circuit breaker currently considers P21 Toolbox's
    /// MCP facade reachable -- backs the call-pipeline status endpoint the Hub polls before
    /// spending Azure Speech budget on a call. Since P21 validation is mandatory for every
    /// notification this pipeline can ever emit, an open circuit here means no prompt can be
    /// produced regardless of anything else being healthy.
    /// </summary>
    bool IsHealthy { get; }
}
