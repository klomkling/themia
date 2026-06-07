using System;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Themia.Quartz;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Dependency-injection entry points for the Themia Quartz dashboard. Ported from SilkierQuartz's
/// <c>AddSilkierQuartz</c>, minus the dropped cookie-authentication and <c>[SilkierQuartz]</c> job
/// auto-discovery wiring — the host owns the Quartz <see cref="global::Quartz.IScheduler"/> and job
/// registration in Themia.
/// </summary>
public static class ThemiaQuartzServiceCollectionExtensions
{
    /// <summary>
    /// Registers the Themia Quartz dashboard services: the configured
    /// <see cref="ThemiaQuartzOptions"/> singleton plus the MVC controllers, Newtonsoft JSON
    /// formatter, and the dashboard assembly as an application part. The host owns the Quartz
    /// <see cref="global::Quartz.IScheduler"/>; supply it via <see cref="ThemiaQuartzOptions.Scheduler"/>
    /// or register an <see cref="global::Quartz.IScheduler"/> in the container.
    /// </summary>
    /// <param name="services">The service collection to add the dashboard services to.</param>
    /// <param name="configure">Configures the dashboard options. Must set
    /// <see cref="ThemiaQuartzOptions.Authorize"/> to grant access — the dashboard is deny-all otherwise.</param>
    /// <returns>The same <paramref name="services"/> instance for chaining.</returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="services"/> or <paramref name="configure"/> is <see langword="null"/>.
    /// </exception>
    public static IServiceCollection AddThemiaQuartz(
        this IServiceCollection services,
        Action<ThemiaQuartzOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new ThemiaQuartzOptions();
        configure(options);
        services.TryAddSingleton(options);

        services
            .AddControllersWithViews()
            .AddNewtonsoftJson()
            .AddApplicationPart(typeof(ThemiaQuartzOptions).Assembly);

        return services;
    }
}
