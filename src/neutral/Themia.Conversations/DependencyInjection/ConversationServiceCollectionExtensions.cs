using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Themia.Conversations.Internal;

namespace Themia.Conversations.DependencyInjection;

/// <summary>DI entry point for the engine-neutral part of <c>Themia.Conversations</c>.</summary>
public static class ConversationServiceCollectionExtensions
{
    /// <summary>
    /// Registers the options, <see cref="IConversationService"/>, the default event sink (a row in <c>conversation_events</c>) and the default-deny <see cref="IConversationAccessPolicy"/> (register your own before or after: the last registration wins).
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
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IConversationEventSink>(sp => new DefaultConversationEventSink(sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<IConversationMasker, RegexConversationMasker>();
        services.TryAddSingleton(sp => new ConversationService(
            sp.GetRequiredService<IConversationDialect>(),
            sp.GetRequiredService<ConversationOptions>(),
            sp.GetRequiredService<IConversationAccessPolicy>(),
            sp.GetRequiredService<IConversationEventSink>(),
            sp.GetRequiredService<IConversationMasker>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ILogger<ConversationService>>()));
        services.TryAddSingleton<IConversationService>(sp => sp.GetRequiredService<ConversationService>());
        return services;
    }

    /// <summary>
    /// Adds guest access on top of <see cref="AddThemiaConversations"/>: <see cref="IConversationGuests"/> (invite by contact) and <see cref="IGuestAccess"/> (exchange a link token for
    /// a session). It needs <c>Themia.Challenges</c> with <see cref="ConversationGuestChallengeExtensions.ConfigureConversationGuestPurpose"/> applied; the host fails at start if either is missing.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Sets the peppers and the session limits.</param>
    /// <exception cref="InvalidOperationException">An option is out of range; raised here, at registration.</exception>
    public static IServiceCollection AddThemiaConversationGuests(this IServiceCollection services, Action<ConversationGuestOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new ConversationGuestOptions();
        configure(options);
        options.Validate();

        services.TryAddSingleton(options);
        services.TryAddScoped<ConversationGuests>();
        services.TryAddScoped<IConversationGuests>(sp => sp.GetRequiredService<ConversationGuests>());
        services.TryAddScoped<IGuestAccess>(sp => sp.GetRequiredService<ConversationGuests>());
        services.AddHostedService<GuestConfigurationValidator>();
        return services;
    }
}
