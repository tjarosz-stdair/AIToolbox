using AIToolbox.Web.Services.CallEntityExtraction;

namespace AIToolbox.Web.Tests.Services;

public sealed class P21ValidationCircuitBreakerTests
{
    [Fact]
    public void IsHealthy_TrueBeforeAnyFailures()
    {
        var breaker = new P21ValidationCircuitBreaker();

        Assert.True(breaker.IsHealthy);
        Assert.False(breaker.IsOpen());
    }

    [Fact]
    public void IsHealthy_FalseAfterFiveConsecutiveFailures()
    {
        var breaker = new P21ValidationCircuitBreaker();

        for (var i = 0; i < 5; i++)
        {
            breaker.RecordFailure();
        }

        Assert.False(breaker.IsHealthy);
        Assert.True(breaker.IsOpen());
    }

    [Fact]
    public void IsHealthy_StaysTrueBelowFailureThreshold()
    {
        var breaker = new P21ValidationCircuitBreaker();

        for (var i = 0; i < 4; i++)
        {
            breaker.RecordFailure();
        }

        Assert.True(breaker.IsHealthy);
        Assert.False(breaker.IsOpen());
    }

    [Fact]
    public void RecordSuccess_ResetsFailureCount()
    {
        var breaker = new P21ValidationCircuitBreaker();

        for (var i = 0; i < 4; i++)
        {
            breaker.RecordFailure();
        }

        breaker.RecordSuccess();
        breaker.RecordFailure();
        breaker.RecordFailure();

        Assert.True(breaker.IsHealthy);
    }

    [Fact]
    public void IsHealthy_DoesNotResetOpenCircuitLikeIsOpenDoesAfterCooldown()
    {
        // IsHealthy is a pure read -- it must never itself flip the breaker back closed the way
        // IsOpen()'s cooldown-elapsed probe-reset does. Since the cooldown is 60s, immediately
        // after tripping, both report the same (open/unhealthy) state -- this pins that IsHealthy
        // doesn't silently perform the probe-reset side effect IsOpen() does.
        var breaker = new P21ValidationCircuitBreaker();

        for (var i = 0; i < 5; i++)
        {
            breaker.RecordFailure();
        }

        var healthyBeforeOpenCheck = breaker.IsHealthy;
        var openBeforeSecondHealthyCheck = breaker.IsOpen();
        var healthyAfterOpenCheck = breaker.IsHealthy;

        Assert.False(healthyBeforeOpenCheck);
        Assert.True(openBeforeSecondHealthyCheck);
        Assert.False(healthyAfterOpenCheck);
    }
}
