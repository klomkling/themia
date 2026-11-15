using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Themia.Conversations.AspNetCore.Internal;

namespace Themia.Conversations.AspNetCore;

/// <summary>Registration and routing for the conversation HTTP surface.</summary>
public static class ConversationHttpExtensions
{
    /// <summary>Registers the options of the conversation endpoints. The host also registers an <see cref="IConversationActorResolver"/>.</summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Adjusts <see cref="ConversationHttpOptions"/>.</param>
    public static IServiceCollection AddThemiaConversationsHttp(this IServiceCollection services, Action<ConversationHttpOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var options = services.AddOptions<ConversationHttpOptions>();
        if (configure is not null)
        {
            options.Configure(configure);
        }

        return services;
    }

    /// <summary>
    /// Maps the guest exchange and the conversation endpoints under <paramref name="prefix"/>. Fails at startup when no
    /// <see cref="IConversationActorResolver"/> is registered.
    /// </summary>
    /// <param name="endpoints">The route builder.</param>
    /// <param name="prefix">The route prefix, for example <c>/api/v1</c>.</param>
    public static RouteGroupBuilder MapThemiaConversations(this IEndpointRouteBuilder endpoints, string prefix)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        var registrations = endpoints.ServiceProvider.GetRequiredService<IServiceProviderIsService>();
        if (!registrations.IsService(typeof(IConversationActorResolver)))
        {
            throw new InvalidOperationException(
                $"MapThemiaConversations needs an {nameof(IConversationActorResolver)}: the package never decides who a caller is. Register one that maps your authentication to a ConversationCaller.");
        }

        var group = endpoints.MapGroup(prefix);
        ConversationEndpoints.Map(group, guestExchange: registrations.IsService(typeof(IGuestAccess)));
        return group;
    }
}
