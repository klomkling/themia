using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Themia.AspNetCore;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.MultiTenancy.AspNet;

namespace Themia.Framework.AspNetCore.Extensions;

/// <summary>
/// Extension methods for setting up the Themia framework in an ASP.NET Core application.
/// </summary>
public static class ThemiaAspNetCoreExtensions
{
    /// <summary>
    /// Adds Themia ASP.NET Core services to the specified <see cref="IServiceCollection"/>.
    /// </summary>
    /// <remarks>
    /// Registers the scoped <see cref="ITenantContext"/> that reads the ambient
    /// <see cref="TenantContextAccessor"/>. Tenant resolution itself (strategies/stores) is configured
    /// separately by the host via <c>AddThemiaMultiTenancy(...)</c>.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddThemiaAspNetCore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<ITenantContext, AspNetCoreTenantContext>();

        return services;
    }

    /// <summary>
    /// Adds the Themia middleware to the request pipeline: the neutral ProblemDetails handler
    /// (outermost, so it maps typed exceptions from everything downstream) followed by tenant
    /// resolution.
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <returns>The application builder.</returns>
    public static IApplicationBuilder UseThemia(this IApplicationBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);

        app.UseThemiaProblemDetails();
        app.UseThemiaMultiTenancy();

        return app;
    }
}

/// <summary>
/// Scoped implementation of <see cref="ITenantContext"/> that bridges the ambient accessor.
/// </summary>
internal sealed class AspNetCoreTenantContext : ITenantContext
{
    /// <inheritdoc />
    public TenantId? CurrentTenantId => TenantContextAccessor.CurrentTenantId;

    /// <inheritdoc />
    public string? Source => "AspNetCore";
}
