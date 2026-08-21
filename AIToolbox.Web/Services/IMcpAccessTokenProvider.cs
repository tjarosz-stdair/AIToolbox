namespace AIToolbox.Web.Services;

public interface IMcpAccessTokenProvider
{
    /// <summary>
    /// Returns a cached machine-client OAuth access token for the given surface's MCP client
    /// credential, minting a new one via client_credentials if none is cached or the cached one
    /// is near expiry. Pass <paramref name="forceRefresh"/> after a 401 from an actual MCP call
    /// to mint exactly one new token rather than retrying indefinitely against a stale one.
    /// </summary>
    Task<string> GetAccessTokenAsync(string surface, bool forceRefresh = false, CancellationToken cancellationToken = default);
}
