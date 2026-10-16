using AIToolbox.Ductwork.Contracts;

namespace AIToolbox.Ductwork.Core.Activity;

public sealed record ActivityTotals(double ActiveSeconds, double IdleSeconds, double UncreditedSeconds, int Heartbeats);

/// <summary>
/// Turns viewer heartbeats into active and idle time for one session (plan section 10.1).
/// Time is computed on the server from receive times, never reported by the client. A heartbeat
/// describes the interval that ends at it, so the gap since the previous heartbeat is credited to
/// the new heartbeat's state. A gap longer than <see cref="MaxCreditedGap"/> (two heartbeat
/// intervals by default) credits only that cap, so a closed laptop or a dead tab accrues nothing
/// beyond it. The remainder is reported as uncredited for diagnostics.
/// </summary>
public sealed class ActivityAccumulator
{
    public static readonly TimeSpan DefaultHeartbeatInterval = TimeSpan.FromSeconds(15);

    private readonly string _sessionId;
    private DateTimeOffset? _last;
    private double _active;
    private double _idle;
    private double _uncredited;
    private int _count;

    public ActivityAccumulator(string sessionId, TimeSpan? heartbeatInterval = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) throw new ArgumentException("A session id is required.", nameof(sessionId));
        _sessionId = sessionId;
        var interval = heartbeatInterval ?? DefaultHeartbeatInterval;
        if (interval <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(heartbeatInterval));
        MaxCreditedGap = interval * 2;
    }

    public TimeSpan MaxCreditedGap { get; }

    public ActivityTotals Totals => new(_active, _idle, _uncredited, _count);

    /// <summary>
    /// Adds one heartbeat and returns the seconds credited by it. A heartbeat that is not later than
    /// the previous one (duplicate or out of order) is ignored and credits nothing.
    /// </summary>
    public double Add(HeartbeatSample sample)
    {
        if (!string.Equals(sample.SessionId, _sessionId, StringComparison.Ordinal))
            throw new ArgumentException("The heartbeat belongs to a different session.", nameof(sample));

        if (_last is { } last && sample.ReceivedUtc <= last)
            return 0;

        double credited = 0;
        if (_last is { } previous)
        {
            var (active, idle, uncredited) = Credit(sample.ReceivedUtc - previous, sample.State, MaxCreditedGap);
            credited = active + idle;
            _active += active;
            _idle += idle;
            _uncredited += uncredited;
        }

        _last = sample.ReceivedUtc;
        _count++;
        return credited;
    }

    /// <summary>
    /// The one rule for turning a gap between heartbeats into time: the gap is credited to the new
    /// heartbeat's state, up to the cap, and the remainder is uncredited. The in-memory accumulator and the
    /// persisted session both use it, so they can never disagree.
    /// </summary>
    public static (double Active, double Idle, double Uncredited) Credit(TimeSpan gap, ActivityState state, TimeSpan maxCreditedGap)
    {
        if (gap < TimeSpan.Zero) gap = TimeSpan.Zero;
        var credited = gap > maxCreditedGap ? maxCreditedGap : gap;
        var uncredited = (gap - credited).TotalSeconds;
        return state == ActivityState.Active ? (credited.TotalSeconds, 0, uncredited) : (0, credited.TotalSeconds, uncredited);
    }

    public static ActivityTotals Compute(string sessionId, IEnumerable<HeartbeatSample> samples, TimeSpan? heartbeatInterval = null)
    {
        var accumulator = new ActivityAccumulator(sessionId, heartbeatInterval);
        foreach (var sample in samples.OrderBy(s => s.ReceivedUtc))
            accumulator.Add(sample);
        return accumulator.Totals;
    }
}
