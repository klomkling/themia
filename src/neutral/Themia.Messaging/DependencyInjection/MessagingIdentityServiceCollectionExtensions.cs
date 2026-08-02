using Microsoft.Extensions.DependencyInjection;

namespace Themia.Messaging.DependencyInjection;

/// <summary>DI entry point for this service's messaging identity.</summary>
public static class MessagingIdentityServiceCollectionExtensions
{
    /// <summary>
    /// Registers this service's <see cref="MessagingIdentity"/>. Call this BEFORE
    /// <c>AddThemiaMessagingModule</c> and <c>AddThemiaMessagingVerification</c>, both of which
    /// require it.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="origin">This service's origin identifier.</param>
    /// <returns>The same <paramref name="services"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    /// <exception cref="ArgumentException"><paramref name="origin"/> is null, empty or whitespace.</exception>
    /// <exception cref="InvalidOperationException">
    /// A <see cref="MessagingIdentity"/> is already registered. A second registration would append a
    /// descriptor rather than replace one, leaving two identities in the container with the later
    /// silently winning — which is the drift this type exists to remove.
    /// </exception>
    public static IServiceCollection AddThemiaMessagingIdentity(this IServiceCollection services, string origin)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentException.ThrowIfNullOrWhiteSpace(origin);

        // ServiceType, not ImplementationInstance: a factory registration carries a null instance, so
        // an instance-scan would miss it and append a second descriptor. Mirrors the same check in
        // AddThemiaMessagingHmac.
        if (services.Any(d => d.ServiceType == typeof(MessagingIdentity)))
        {
            throw new InvalidOperationException(
                "A MessagingIdentity is already registered. This service has exactly one identity, and a "
                + "second registration would leave two in the container with the later silently winning. "
                + "Call AddThemiaMessagingIdentity(...) once, in one place.");
        }

        services.AddSingleton(new MessagingIdentity(origin));
        return services;
    }
}
