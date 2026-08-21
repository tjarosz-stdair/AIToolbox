using AIToolbox.Web.Models;

namespace AIToolbox.Web.Data;

public interface IMcpClientCredentialRepository
{
    Task<IReadOnlyList<McpClientCredential>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<McpClientCredential?> GetByIdAsync(int id, CancellationToken cancellationToken = default);

    Task<McpClientCredential?> GetBySurfaceAsync(string surface, CancellationToken cancellationToken = default);

    Task<int> CreateAsync(McpClientCredential credential, CancellationToken cancellationToken = default);

    Task UpdateAsync(McpClientCredential credential, bool updateSecret, CancellationToken cancellationToken = default);
}
