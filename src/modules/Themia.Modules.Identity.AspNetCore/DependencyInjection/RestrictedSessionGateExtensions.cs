using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Themia.Modules.Identity.AspNetCore.Authentication;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>DI entry point for the restricted-session gate.</summary>
public static class RestrictedSessionGateExtensions
{
    /// <summary>
    /// Refuses a restricted session (after a recovery-code sign-in, or while an upgrade is due) with a 403 problem
    /// (<c>code: second_factor_restricted</c>) on every authorized endpoint that is not marked
    /// <see cref="AllowRestrictedSessionAttribute"/>. Wraps whichever <see cref="IAuthorizationMiddlewareResultHandler"/> is registered
    /// at this point, so call it after any custom handler. Needs <c>UseAuthorization</c> and the session proof store from
    /// <c>AddThemiaIdentitySecondFactor</c>; with no store registered every restricted session is refused.
    /// </summary>
    /// <param name="services">The service collection.</param>
    public static IServiceCollection AddThemiaRestrictedSessionGate(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // The gate is a factory registration, so it cannot be recognised by type. A repeat call is a no-op only while the
        // gate's own descriptor is still the last handler: if a custom handler was registered after it, the gate is out of
        // the chain and must wrap that handler again, never be skipped.
        var last = services.LastOrDefault(descriptor => descriptor.ServiceType == typeof(IAuthorizationMiddlewareResultHandler));
        var marker = services.Select(descriptor => descriptor.ImplementationInstance).OfType<RestrictedSessionGateMarker>().LastOrDefault();
        if (marker is not null && ReferenceEquals(marker.Gate, last))
        {
            return services;
        }

        var gate = ServiceDescriptor.Singleton<IAuthorizationMiddlewareResultHandler>(provider =>
            new RestrictedSessionGate(Inner(provider, last), provider.GetRequiredService<ILogger<RestrictedSessionGate>>()));
        services.AddSingleton(new RestrictedSessionGateMarker(gate));
        services.Add(gate);
        return services;
    }

    private static IAuthorizationMiddlewareResultHandler Inner(IServiceProvider provider, ServiceDescriptor? previous)
    {
        if (previous is null)
        {
            return new AuthorizationMiddlewareResultHandler();
        }

        if (previous.ImplementationInstance is IAuthorizationMiddlewareResultHandler instance)
        {
            return instance;
        }

        if (previous.ImplementationFactory is { } factory)
        {
            return (IAuthorizationMiddlewareResultHandler)factory(provider);
        }

        return (IAuthorizationMiddlewareResultHandler)ActivatorUtilities.CreateInstance(provider, previous.ImplementationType!);
    }

    private sealed record RestrictedSessionGateMarker(ServiceDescriptor Gate);
}
