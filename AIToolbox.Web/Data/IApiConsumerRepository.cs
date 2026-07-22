using AIToolbox.Web.Models;

namespace AIToolbox.Web.Data;

public interface IApiConsumerRepository
{
    Task<IReadOnlyList<ApiConsumer>> GetConsumersAsync(CancellationToken cancellationToken = default);
    Task<ApiConsumer?> GetConsumerAsync(int id, CancellationToken cancellationToken = default);
    Task<int> CreateConsumerAsync(ApiConsumer consumer, CancellationToken cancellationToken = default);
    Task UpdateConsumerAsync(ApiConsumer consumer, CancellationToken cancellationToken = default);
    Task<ApiConsumerKey?> GetKeyByPrefixAsync(string keyPrefix, CancellationToken cancellationToken = default);
    Task<int> CreateKeyAsync(ApiConsumerKey key, CancellationToken cancellationToken = default);
    Task RevokeKeyAsync(int keyId, CancellationToken cancellationToken = default);
    Task MarkKeyUsedAsync(int keyId, CancellationToken cancellationToken = default);
}
