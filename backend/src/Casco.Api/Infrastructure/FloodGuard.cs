using System.Threading.RateLimiting;

namespace Casco.Api.Infrastructure;

/// <summary>
/// Caps every request, including published sites and static files that never reach an endpoint policy.
/// Runs after forwarded headers so the key is the visitor IP Caddy recorded, not the proxy.
/// </summary>
public sealed class FloodGuardMiddleware(RequestDelegate next, PartitionedRateLimiter<HttpContext> limiter)
{
    public const int PermitsPerMinute = 300;

    public async Task InvokeAsync(HttpContext ctx)
    {
        var path = ctx.Request.Path.Value ?? "";
        if (HttpMethods.IsOptions(ctx.Request.Method)
            || path.Equals("/health", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/internal/", StringComparison.OrdinalIgnoreCase))
        {
            await next(ctx);
            return;
        }

        using var lease = await limiter.AcquireAsync(ctx, 1, ctx.RequestAborted);
        if (lease.IsAcquired)
        {
            await next(ctx);
            return;
        }

        ctx.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        ctx.Response.Headers.RetryAfter = "60";
        if (path.StartsWith("/api", StringComparison.OrdinalIgnoreCase))
            await ctx.Response.WriteAsJsonAsync(new { message = "طلبات كثيرة، انتظر قليلاً ثم حاول", code = "rate_limited" }, ctx.RequestAborted);
    }
}
