using AIToolbox.Web.Models;

namespace AIToolbox.Web.Security;

public interface IApiKeyService
{
    string GenerateRawKey();
    string GetPrefix(string rawKey);
    string HashKey(string rawKey);
    Task<ApiConsumerPrincipal?> ValidateAsync(string rawKey, CancellationToken cancellationToken = default);
}
