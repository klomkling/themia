using System;
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
    /// <see cref="ThemiaQuartzOptions"/> singleton plus the MVC controllers (using the default
    /// System.Text.Json formatter) and the dashboard assembly as an application part. The host owns
    /// the Quartz <see cref="global::Quartz.IScheduler"/>; supply it via
    /// <see cref="ThemiaQuartzOptions.Scheduler"/> or register an
    /// <see cref="global::Quartz.IScheduler"/> in the container.
    /// </summary>
    /// <para>Calling this more than once is safe and <strong>additive</strong>: every <paramref name="configure"/>
    /// delegate is applied to the same options instance, in call order (so the last writer of a given
    /// property wins). This lets a module wire routing/authorization while the host app independently sets
    /// appearance — e.g. <c>Themia.Modules.Scheduling</c> sets <see cref="ThemiaQuartzOptions.VirtualPathRoot"/>
    /// and <see cref="ThemiaQuartzOptions.Authorize"/>, and the app then sets
    /// <see cref="ThemiaQuartzOptions.HeadHtml"/>/<see cref="ThemiaQuartzOptions.CustomStyleSheet"/>.</para>
    /// <param name="services">The service collection to add the dashboard services to.</param>
    /// <param name="configure">Configures the dashboard options. Must set
    /// <see cref="ThemiaQuartzOptions.Authorize"/> to grant access — the dashboard is deny-all otherwise
    /// (across all calls: it is enough that <em>one</em> of them sets it).</param>
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

        // Compose across calls instead of first-one-wins. Themia.Modules.Scheduling calls this to wire
        // VirtualPathRoot/Authorize and the host app calls it to set appearance; with TryAddSingleton over
        // a fresh instance, whichever ran first won and the other's settings vanished silently — so a
        // module consumer could not configure the dashboard at all. Apply every delegate to one instance.
        var options = ExistingOptions(services) ?? RegisterOptions(services);
        configure(options);

        services
            .AddControllersWithViews()
            .AddApplicationPart(typeof(ThemiaQuartzOptions).Assembly);

        return services;
    }

    private static ThemiaQuartzOptions? ExistingOptions(IServiceCollection services)
    {
        foreach (var descriptor in services)
        {
            if (descriptor.ServiceType == typeof(ThemiaQuartzOptions) &&
                descriptor.ImplementationInstance is ThemiaQuartzOptions existing)
            {
                return existing;
            }
        }

        return null;
    }

    private static ThemiaQuartzOptions RegisterOptions(IServiceCollection services)
    {
        var options = new ThemiaQuartzOptions();
        services.AddSingleton(options);
        return options;
    }
}
