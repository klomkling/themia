using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.MultiTenancy.Abstractions;

namespace Themia.MultiTenancy.AspNet;

/// <summary>
/// Middleware that resolves the tenant from the incoming HTTP request, stores it in
/// <see cref="ITenantAccessor"/> (the rich model carrying the per-tenant connection string), and
/// bridges the resolved identifier into <see cref="TenantContextAccessor"/> so the data layer's
/// tenant query filter keys off the same tenant.
/// </summary>
public sealed class TenantResolutionMiddleware
{
    private readonly RequestDelegate _next;

    /// <summary>
    /// Initializes a new instance of the <see cref="TenantResolutionMiddleware"/> class.
    /// </summary>
    public TenantResolutionMiddleware(RequestDelegate next) =>
        _next = next ?? throw new ArgumentNullException(nameof(next));

    /// <summary>
    /// Resolves the tenant for the current request and invokes the next middleware.
    /// </summary>
    public async Task InvokeAsync(HttpContext context)
    {
        if (context is null)
        {
            throw new ArgumentNullException(nameof(context));
        }

        var resolver = context.RequestServices.GetRequiredService<ITenantResolver>();
        var accessor = context.RequestServices.GetRequiredService<ITenantAccessor>();
        var logger = context.RequestServices.GetRequiredService<ILogger<TenantResolutionMiddleware>>();

        var resolution = await resolver.ResolveAsync(new TenantResolutionContext(
            context.Request.Host.Host,
            context.Request.Path.Value ?? string.Empty,
            context.Request.Headers.ToDictionary(h => h.Key, h => h.Value.ToString(), StringComparer.OrdinalIgnoreCase),
            context.User.Claims.GroupBy(c => c.Type).ToDictionary(g => g.Key, g => g.First().Value)), context.RequestAborted);

        if (resolution is null)
        {
            logger.LogWarning("Tenant resolution returned null. Ensure strategies are configured.");
        }

        accessor.Current = resolution;

        // [INTRODUCED] Bridge the resolved tenant into the framework's ambient tenant context so the
        // Tier-3 EF Core global query filter keys off the same tenant. Dropped: v1's
        // TenantContext<string, string> hydration (that internal generic type is not ported).
        TenantContextAccessor.CurrentTenantId = TenantId.From(resolution?.Identifier);

        await _next(context);
    }
}

/// <summary>
/// Extension methods for registering the tenant resolution middleware.
/// </summary>
public static class TenantResolutionMiddlewareExtensions
{
    /// <summary>
    /// Adds the Themia tenant resolution middleware to the application pipeline.
    /// </summary>
    public static IApplicationBuilder UseThemiaMultiTenancy(this IApplicationBuilder app)
    {
        if (app is null)
        {
            throw new ArgumentNullException(nameof(app));
        }

        return app.UseMiddleware<TenantResolutionMiddleware>();
    }
}
