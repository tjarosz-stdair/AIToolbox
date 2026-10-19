using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace AIToolbox.Ductwork.Core.Progress;

/// <summary>
/// One thing on a job that is making progress or has just changed: a processing task or an archive operation. It carries what a progress bar needs and
/// nothing about the job's contents; the page that receives it re-reads the job through the normal API, which checks access again.
/// </summary>
public sealed record JobProgressEvent(long JobId, string Kind, string Id, string Status, long Done, long? Total, string? Message, int Attempt)
{
    public const string TaskKind = "task";
    public const string ArchiveKind = "archive";

    /// <summary>Finished for good: nothing more will change about it unless it is queued again.</summary>
    public bool IsTerminal => Status is "Succeeded" or "Failed" or "Cancelled";
}

/// <summary>Where changes go. The web app delivers them to the browsers watching the job; tests collect them.</summary>
public interface IJobProgressSink
{
    Task SendAsync(long jobId, IReadOnlyList<JobProgressEvent> events, CancellationToken cancellationToken);
}

/// <summary>
/// Watches the processing tasks and archive operations of the jobs somebody has open, and reports each change once. It reads only the jobs being watched,
/// with one query per kind per poll, so it costs nothing when nobody is looking and does not scale with the number of jobs on the instance.
///
/// It watches the database rather than being told by the code that changes it, so a change is reported whoever made it: a worker's heartbeat, a lease that
/// expired, a retry, a cancel, the archive runner. A job's first poll after it is watched reports only what is still active; what had already finished is
/// left to the page's own read, so opening a long-lived job does not send its whole history.
/// </summary>
public sealed class JobProgressTracker(Func<CancellationToken, Task<SqlConnection>> open, IJobProgressSink sink)
{
    private readonly object _gate = new();
    private readonly Dictionary<long, int> _watchers = [];
    private readonly Dictionary<long, Dictionary<string, JobProgressEvent>> _known = [];
    private readonly HashSet<long> _primed = [];

    /// <summary>The jobs somebody is watching right now.</summary>
    public IReadOnlyCollection<long> WatchedJobs
    {
        get { lock (_gate) return _watchers.Keys.ToList(); }
    }

    public void Watch(long jobId)
    {
        lock (_gate) _watchers[jobId] = _watchers.GetValueOrDefault(jobId) + 1;
    }

    public void Unwatch(long jobId)
    {
        lock (_gate)
        {
            if (!_watchers.TryGetValue(jobId, out var count)) return;
            if (count > 1)
            {
                _watchers[jobId] = count - 1;
                return;
            }

            _watchers.Remove(jobId);
            _known.Remove(jobId);
            _primed.Remove(jobId);
        }
    }

    /// <summary>Reads the watched jobs once and sends what changed since the last read. Returns how many events were sent.</summary>
    public async Task<int> PollOnceAsync(CancellationToken cancellationToken = default)
    {
        long[] jobs;
        lock (_gate) jobs = [.. _watchers.Keys];
        if (jobs.Length == 0) return 0;

        var current = await ReadAsync(jobs, cancellationToken);
        var sent = 0;
        foreach (var jobId in jobs)
        {
            var now = current.Where(e => e.JobId == jobId).ToList();
            List<JobProgressEvent> changed;
            lock (_gate)
            {
                // The job may have been dropped while the query ran: its new watcher must not inherit this read.
                if (!_watchers.ContainsKey(jobId)) continue;
                var known = _known.TryGetValue(jobId, out var k) ? k : _known[jobId] = [];
                var first = _primed.Add(jobId);
                changed = [];
                foreach (var e in now)
                {
                    var key = e.Kind + ":" + e.Id;
                    var same = known.TryGetValue(key, out var before) && before == e;
                    known[key] = e;
                    if (same || (first && e.IsTerminal)) continue;
                    changed.Add(e);
                }
            }

            if (changed.Count == 0) continue;
            await sink.SendAsync(jobId, changed, cancellationToken);
            sent += changed.Count;
        }

        return sent;
    }

    private async Task<List<JobProgressEvent>> ReadAsync(long[] jobs, CancellationToken cancellationToken)
    {
        var ids = JsonSerializer.Serialize(jobs);
        var events = new List<JobProgressEvent>();
        await using var connection = await open(cancellationToken);

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT ProcessingTaskId, JobId, Status, ProgressDone, ProgressTotal, ProgressMessage, Attempt
                FROM ductwork.ProcessingTask WHERE JobId IN (SELECT CAST([value] AS BIGINT) FROM OPENJSON(@ids));
                """;
            command.Parameters.AddWithValue("@ids", ids);
            await using var r = await command.ExecuteReaderAsync(cancellationToken);
            while (await r.ReadAsync(cancellationToken))
                events.Add(new JobProgressEvent(r.GetInt64(1), JobProgressEvent.TaskKind, r.GetInt64(0).ToString(), r.GetString(2), r.GetInt32(3),
                    r.IsDBNull(4) ? null : r.GetInt32(4), r.IsDBNull(5) ? null : r.GetString(5), r.GetInt32(6)));
        }

        await using (var command = connection.CreateCommand())
        {
            command.CommandText =
                """
                SELECT PublicId, JobId, Status, ProgressDone, ProgressTotal, Message, Attempt
                FROM ductwork.ArchiveOperation WHERE JobId IN (SELECT CAST([value] AS BIGINT) FROM OPENJSON(@ids));
                """;
            command.Parameters.AddWithValue("@ids", ids);
            await using var r = await command.ExecuteReaderAsync(cancellationToken);
            while (await r.ReadAsync(cancellationToken))
                events.Add(new JobProgressEvent(r.GetInt64(1), JobProgressEvent.ArchiveKind, r.GetGuid(0).ToString("D"), r.GetString(2), r.GetInt64(3),
                    r.IsDBNull(4) ? null : r.GetInt64(4), r.IsDBNull(5) ? null : r.GetString(5), r.GetInt32(6)));
        }

        return events;
    }
}
