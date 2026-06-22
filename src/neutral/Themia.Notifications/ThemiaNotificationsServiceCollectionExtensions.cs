using Microsoft.Extensions.DependencyInjection.Extensions;
using Themia.Notifications;
using Themia.Notifications.Providers;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>DI registration for the Themia notification core.</summary>
public static class ThemiaNotificationsServiceCollectionExtensions
{
    /// <summary>Registers the notification renderer + options and the logger-stub senders as defaults
    /// (<c>TryAdd</c> — a host-registered real sender wins). Idempotent.</summary>
    public static IServiceCollection AddThemiaNotifications(
        this IServiceCollection services,
        Action<ThemiaNotificationsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new ThemiaNotificationsOptions();
        configure?.Invoke(options);

        services.TryAddSingleton(options);
        services.TryAddSingleton<INotificationTemplateRenderer, HandlebarsNotificationRenderer>();
        services.TryAddSingleton<IEmailSender, LoggerEmailSender>();
        services.TryAddSingleton<ISmsSender, LoggerSmsSender>();
        return services;
    }
}
