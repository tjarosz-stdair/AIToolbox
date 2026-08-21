namespace AIToolbox.Web.Security;

/// <summary>
/// Default-deny framing headers app-wide, with /embed as the one carve-out (EmbedController sets
/// its own frame-ancestors there instead). This is the app's first framing policy at all — no
/// X-Frame-Options/CSP frame-ancestors existed anywhere before this, so default-deny-with-one-
/// carve-out is the only defensible starting posture.
/// </summary>
public sealed class FrameGuardMiddleware
{
    private readonly RequestDelegate _next;

    public FrameGuardMiddleware(RequestDelegate next)
    {
        _next = next;
    }

    public Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/embed", StringComparison.OrdinalIgnoreCase))
        {
            // OnStarting fires just before headers are sent, so this still lands on a
            // redirect/401 response, not only a normal 200.
            context.Response.OnStarting(() =>
            {
                context.Response.Headers["X-Frame-Options"] = "DENY";
                context.Response.Headers["Content-Security-Policy"] = "frame-ancestors 'none'";
                return Task.CompletedTask;
            });
        }

        return _next(context);
    }
}
