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
}
