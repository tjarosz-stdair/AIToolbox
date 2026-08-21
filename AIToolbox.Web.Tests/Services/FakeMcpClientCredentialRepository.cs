using AIToolbox.Web.Data;
using AIToolbox.Web.Models;

namespace AIToolbox.Web.Tests.Services;

public sealed class FakeMcpClientCredentialRepository : IMcpClientCredentialRepository
{
    private readonly McpClientCredential? _credential;

    public FakeMcpClientCredentialRepository(McpClientCredential? credential)
    {
        _credential = credential;
    }

    public Task<IReadOnlyList<McpClientCredential>> GetAllAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult<IReadOnlyList<McpClientCredential>>(_credential is null ? [] : [_credential]);

    public Task<McpClientCredential?> GetByIdAsync(int id, CancellationToken cancellationToken = default) =>
        Task.FromResult(_credential);

    public Task<McpClientCredential?> GetBySurfaceAsync(string surface, CancellationToken cancellationToken = default) =>
        Task.FromResult(_credential is not null && _credential.Surface == surface ? _credential : null);

    public Task<int> CreateAsync(McpClientCredential credential, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task UpdateAsync(McpClientCredential credential, bool updateSecret, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}
