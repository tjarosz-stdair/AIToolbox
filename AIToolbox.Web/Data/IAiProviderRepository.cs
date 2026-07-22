using AIToolbox.Web.Models;

namespace AIToolbox.Web.Data;

public interface IAiProviderRepository
{
    Task<IReadOnlyList<AiProvider>> GetAllAsync(bool includeDisabled = false, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AiProvider>> GetEnabledByIdsAsync(IEnumerable<int> ids, CancellationToken cancellationToken = default);

    Task<AiProvider?> GetByIdAsync(int id, CancellationToken cancellationToken = default);

    Task<int> CreateAsync(AiProvider provider, CancellationToken cancellationToken = default);

    Task UpdateAsync(AiProvider provider, bool updateSecret, CancellationToken cancellationToken = default);
}
