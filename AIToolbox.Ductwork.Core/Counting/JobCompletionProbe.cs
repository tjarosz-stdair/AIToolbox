using AIToolbox.Ductwork.Core.Jobs;
using Microsoft.Data.SqlClient;

namespace AIToolbox.Ductwork.Core.Counting;

/// <summary>
/// The facts that decide whether a job may be completed or closed (plan section 8.2): work still queued or
/// running, and building types that have counted items but a multiplier no person has confirmed.
/// </summary>
public sealed class JobCompletionProbe : ICompletionProbe
{
    public async Task<(int ActiveProcessingTasks, int UnconfirmedMultipliers)> GetAsync(
        SqlConnection connection, SqlTransaction transaction, long jobId, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText =
            """
            SELECT
                (SELECT COUNT(*) FROM ductwork.ProcessingTask WHERE JobId = @j AND Status IN ('Queued', 'Running')),
                (SELECT COUNT(*) FROM ductwork.BuildingType b
                 WHERE b.JobId = @j AND b.Confirmed = 0
                   AND EXISTS (SELECT 1 FROM ductwork.Entity e
                               WHERE e.BuildingTypeId = b.BuildingTypeId AND e.Disposition = 'Included' AND e.ReviewStatus <> 'Rejected'));
            """;
        command.Parameters.AddWithValue("@j", jobId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return (reader.GetInt32(0), reader.GetInt32(1));
    }
}
