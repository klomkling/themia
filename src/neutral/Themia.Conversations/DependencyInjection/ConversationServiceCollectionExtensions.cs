using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Themia.Conversations.Internal;

namespace Themia.Conversations.DependencyInjection;

/// <summary>DI entry point for the engine-neutral part of <c>Themia.Conversations</c>.</summary>
public static class ConversationServiceCollectionExtensions
{
    /// <summary>
    /// Registers the options and the default-deny <see cref="IConversationAccessPolicy"/> (register your own before or after: the last registration wins).
    /// Call the engine package's registration as well (for example <c>AddThemiaConversationsPostgres</c>), which registers the dialect and runs the migration.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Changes the limits and retention.</param>
    /// <exception cref="InvalidOperationException">An option is out of range; raised here, at registration, not at the first message.</exception>
    public static IServiceCollection AddThemiaConversations(this IServiceCollection services, Action<ConversationOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new ConversationOptions();
        configure?.Invoke(options);
        options.Validate();

        services.TryAddSingleton(options);
        services.TryAddSingleton<IConversationAccessPolicy, DenyAllConversationAccessPolicy>();
        return services;
    }
}
