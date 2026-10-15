using AIToolbox.Ductwork.Contracts;
using AIToolbox.Ductwork.Core.Activity;

namespace AIToolbox.Ductwork.Tests;

public class ActivityAccumulatorTests
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);

    private static HeartbeatSample Beat(double seconds, ActivityState state, string session = "s1") =>
        new() { SessionId = session, State = state, ReceivedUtc = T0.AddSeconds(seconds) };

    [Fact]
    public void First_heartbeat_credits_nothing()
    {
        var acc = new ActivityAccumulator("s1");
        Assert.Equal(0, acc.Add(Beat(0, ActivityState.Active)));
        Assert.Equal(0, acc.Totals.ActiveSeconds);
        Assert.Equal(1, acc.Totals.Heartbeats);
    }

    [Fact]
    public void Gap_is_credited_to_the_state_of_the_heartbeat_that_ends_it()
    {
        var acc = new ActivityAccumulator("s1");
        acc.Add(Beat(0, ActivityState.Active));
        acc.Add(Beat(15, ActivityState.Active));
        acc.Add(Beat(30, ActivityState.Idle));
        acc.Add(Beat(45, ActivityState.Active));

        var totals = acc.Totals;
        Assert.Equal(30, totals.ActiveSeconds);
        Assert.Equal(15, totals.IdleSeconds);
        Assert.Equal(0, totals.UncreditedSeconds);
    }

    [Fact]
    public void Long_gap_is_capped_at_two_intervals_and_the_rest_is_uncredited()
    {
        var acc = new ActivityAccumulator("s1");
        acc.Add(Beat(0, ActivityState.Active));
        // Laptop closed for ten minutes.
        acc.Add(Beat(600, ActivityState.Active));

        Assert.Equal(30, acc.Totals.ActiveSeconds);
        Assert.Equal(570, acc.Totals.UncreditedSeconds);
    }

    [Fact]
    public void Duplicates_and_out_of_order_heartbeats_are_ignored()
    {
        var acc = new ActivityAccumulator("s1");
        acc.Add(Beat(0, ActivityState.Active));
        acc.Add(Beat(15, ActivityState.Active));

        Assert.Equal(0, acc.Add(Beat(15, ActivityState.Active)));
        Assert.Equal(0, acc.Add(Beat(5, ActivityState.Active)));
        Assert.Equal(15, acc.Totals.ActiveSeconds);
        Assert.Equal(2, acc.Totals.Heartbeats);
    }

    [Fact]
    public void Heartbeat_from_another_session_is_refused()
    {
        var acc = new ActivityAccumulator("s1");
        Assert.Throws<ArgumentException>(() => acc.Add(Beat(0, ActivityState.Active, "other")));
    }

    [Fact]
    public void Compute_sorts_unordered_input()
    {
        var totals = ActivityAccumulator.Compute("s1",
        [
            Beat(30, ActivityState.Active),
            Beat(0, ActivityState.Active),
            Beat(15, ActivityState.Idle)
        ]);

        Assert.Equal(15, totals.ActiveSeconds);
        Assert.Equal(15, totals.IdleSeconds);
    }

    [Fact]
    public void Credit_never_exceeds_wall_clock_time()
    {
        var samples = Enumerable.Range(0, 200)
            .Select(i => Beat(i * 15 + (i % 7), i % 3 == 0 ? ActivityState.Idle : ActivityState.Active))
            .ToList();
        var totals = ActivityAccumulator.Compute("s1", samples);
        var wall = (samples[^1].ReceivedUtc - samples[0].ReceivedUtc).TotalSeconds;

        Assert.True(totals.ActiveSeconds + totals.IdleSeconds <= wall + 1e-9);
        Assert.Equal(wall, totals.ActiveSeconds + totals.IdleSeconds + totals.UncreditedSeconds, 6);
    }

    [Fact]
    public void Blank_session_id_is_refused()
    {
        Assert.Throws<ArgumentException>(() => new ActivityAccumulator(" "));
    }
}
