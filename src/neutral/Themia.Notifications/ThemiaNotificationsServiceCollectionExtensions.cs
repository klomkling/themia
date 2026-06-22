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

    /// <summary>Replaces the default email sender with <c>SmtpEmailSender</c> over the configured
    /// <see cref="Themia.Notifications.Providers.SmtpEmailOptions"/>. Call alongside
    /// <see cref="AddThemiaNotifications"/> to enable real SMTP delivery.</summary>
    public static IServiceCollection AddThemiaSmtpEmailSender(
        this IServiceCollection services,
        Action<SmtpEmailOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configure);

        // Ensure the renderer SmtpEmailSender depends on exists even if AddThemiaNotifications wasn't
        // called first; TryAdd means a host-configured renderer/options still win.
        services.TryAddSingleton(new ThemiaNotificationsOptions());
        services.TryAddSingleton<INotificationTemplateRenderer, HandlebarsNotificationRenderer>();

        var options = new SmtpEmailOptions();
        configure(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.FromAddress);
        // Host is required for real delivery; ignored only when writing to a pickup directory (dev/test).
        if (string.IsNullOrWhiteSpace(options.PickupDirectory))
            ArgumentException.ThrowIfNullOrWhiteSpace(options.Host);
        services.Replace(ServiceDescriptor.Singleton(options));
        services.Replace(ServiceDescriptor.Singleton<IEmailSender, SmtpEmailSender>());
        return services;
    }
}
