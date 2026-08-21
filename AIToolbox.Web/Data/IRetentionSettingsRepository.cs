using AIToolbox.Web.Models;

namespace AIToolbox.Web.Data;

public interface IRetentionSettingsRepository
{
    Task<RetentionSettings> GetAsync(CancellationToken cancellationToken = default);

    Task UpdateAsync(int conversationRetentionDays, CancellationToken cancellationToken = default);
}
