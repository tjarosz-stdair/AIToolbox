namespace AIToolbox.Web.Services.CallEntityExtraction;

/// <summary>
/// The circuit-breaker state P21IdentifierValidationClient trips on repeated P21 MCP failures,
/// pulled out into its own singleton. P21IdentifierValidationClient itself is registered Scoped
/// (CallEntityExtractionBackgroundService resolves it from a fresh IServiceScopeFactory scope per
/// dequeued call, since its other dependency IP21McpClientFactory is itself Scoped) -- if these
/// counters lived on that class directly, they'd reset to "healthy" on every new scope, and the
/// breaker would never actually accumulate failures across calls. Singleton services can be
/// injected into scoped ones safely (the reverse -- a singleton capturing a scoped dependency --
/// is the actual anti-pattern), so this is injected into P21IdentifierValidationClient instead.
/// </summary>
public sealed class P21ValidationCircuitBreaker
{
    private const int FailureThreshold = 5;
    private static readonly TimeSpan Cooldown = TimeSpan.FromSeconds(60);

    private readonly object _gate = new();
    private int _consecutiveFailures;
    private DateTime? _openUntilUtc;

    /// <summary>
    /// True if the next call should be let through. Not side-effect-free: once the cooldown has
    /// elapsed, this resets the counters so the next call is treated as a probe -- RecordSuccess/
    /// RecordFailure then decide whether the circuit re-closes or re-opens. Only ValidateAsync's
    /// own gate should call this -- a passive health check must use IsHealthy instead, or every
    /// poll would itself count as a probe attempt without ever making a real P21 call.
    /// </summary>
    public bool IsOpen()
    {
        lock (_gate)
        {
            if (_openUntilUtc is null)
            {
                return false;
            }

            if (DateTime.UtcNow < _openUntilUtc)
            {
                return true;
            }

            _openUntilUtc = null;
            _consecutiveFailures = 0;
            return false;
        }
    }

    /// <summary>Side-effect-free snapshot for the call-pipeline status endpoint.</summary>
    public bool IsHealthy
    {
        get
        {
            lock (_gate)
            {
                return _openUntilUtc is null || DateTime.UtcNow >= _openUntilUtc;
            }
        }
    }

    public void RecordSuccess()
    {
        lock (_gate)
        {
            _consecutiveFailures = 0;
            _openUntilUtc = null;
        }
    }

    public void RecordFailure()
    {
        lock (_gate)
        {
            _consecutiveFailures++;
            if (_consecutiveFailures >= FailureThreshold)
            {
                _openUntilUtc = DateTime.UtcNow + Cooldown;
            }
        }
    }
}
