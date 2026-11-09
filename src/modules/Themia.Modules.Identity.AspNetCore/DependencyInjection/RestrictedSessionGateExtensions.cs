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

        var previous = services.LastOrDefault(descriptor => descriptor.ServiceType == typeof(IAuthorizationMiddlewareResultHandler));
        if (previous?.ImplementationType == typeof(RestrictedSessionGate))
        {
            return services;
        }

        services.Add(ServiceDescriptor.Singleton<IAuthorizationMiddlewareResultHandler>(provider =>
            new RestrictedSessionGate(Inner(provider, previous), provider.GetRequiredService<ILogger<RestrictedSessionGate>>())));
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
}
