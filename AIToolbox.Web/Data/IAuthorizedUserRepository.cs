using AIToolbox.Web.Models;

namespace AIToolbox.Web.Data;

public interface IAuthorizedUserRepository
{
    Task<IReadOnlyList<AuthorizedUser>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<AuthorizedUser?> GetByEmailAsync(string email, CancellationToken cancellationToken = default);

    Task<AuthorizedUser?> GetByIdAsync(int id, CancellationToken cancellationToken = default);

    Task<int> CreateAsync(AuthorizedUser user, CancellationToken cancellationToken = default);

    Task UpdateAsync(AuthorizedUser user, CancellationToken cancellationToken = default);

    Task MarkLastLoginAsync(string email, CancellationToken cancellationToken = default);
}
