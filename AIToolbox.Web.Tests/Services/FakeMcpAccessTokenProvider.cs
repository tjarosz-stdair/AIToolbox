using AIToolbox.Web.Services;

namespace AIToolbox.Web.Tests.Services;

public sealed class FakeMcpAccessTokenProvider : IMcpAccessTokenProvider
{
    public Task<string> GetAccessTokenAsync(string surface, bool forceRefresh = false, CancellationToken cancellationToken = default) =>
        Task.FromResult("test-access-token");
}
