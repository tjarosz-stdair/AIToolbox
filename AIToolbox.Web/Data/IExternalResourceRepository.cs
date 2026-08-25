using AIToolbox.Web.Models;

namespace AIToolbox.Web.Data;

public interface IExternalResourceRepository
{
    Task<IReadOnlyList<ExternalResource>> GetAllAsync(bool includeDisabled = false, CancellationToken cancellationToken = default);

    Task<ExternalResource?> GetByIdAsync(int id, CancellationToken cancellationToken = default);

    /// <summary>The one enabled resource of this type, if any — external resources aren't
    /// assigned to a ToolKey the way AiProviders are, so there's just at most one enabled
    /// resource per type to find.</summary>
    Task<ExternalResource?> GetEnabledByTypeAsync(ExternalResourceType type, CancellationToken cancellationToken = default);

    Task<int> CreateAsync(ExternalResource resource, CancellationToken cancellationToken = default);

    Task UpdateAsync(ExternalResource resource, bool updateSecret, CancellationToken cancellationToken = default);
}
