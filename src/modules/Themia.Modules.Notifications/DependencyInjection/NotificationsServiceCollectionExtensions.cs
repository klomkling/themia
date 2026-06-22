using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Themia.Framework.Data.Dapper.Mapping;
using Themia.Modules.Notifications.Config;
using Themia.Modules.Notifications.Dispatch;
using Themia.Modules.Notifications.Mapping;
using Themia.Modules.Notifications.Outbox;
using Themia.Modules.Notifications.Stores;

namespace Themia.Modules.Notifications.DependencyInjection;

/// <summary>Registers the Themia Notifications module services (outbox, drainer, dispatcher, stores, resolvers).</summary>
public static class NotificationsServiceCollectionExtensions
{
    /// <summary>
    /// Adds the Notifications module's own services: the peer-agnostic stores, outbox store, preference and
    /// provider-config resolvers, the dispatcher, the <see cref="DrainSignal"/>, and the background
    /// <c>OutboxDrainer</c> hosted service. The adopter must ALSO register: (1) a provider dialect via
    /// <c>AddThemiaNotifications{PostgreSql|MySql|SqlServer}(...)</c> (the drainer needs <c>INotificationsSqlDialect</c>);
    /// (2) the neutral senders via <c>AddThemiaNotifications(...)</c> (the drainer needs <c>IEmailSender</c> etc.);
    /// (3) a framework data peer (EF with <c>modelBuilder.ApplyThemiaNotifications()</c>, or Dapper); and run the
    /// module's <see cref="NotificationsModule.InitializeAsync"/> to apply the schema migration.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional callback to configure <see cref="NotificationsModuleOptions"/>.</param>
    /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection AddThemiaNotificationsModule(
        this IServiceCollection services,
        Action<NotificationsModuleOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new NotificationsModuleOptions();
        configure?.Invoke(options);
        options.Validate();

        services.AddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.AddLogging();

        services.TryAddSingleton<DrainSignal>();

        services.TryAddScoped<IOutboxStore, OutboxStore>();
        services.TryAddScoped<IInAppNotificationStore, InAppNotificationStore>();
        services.TryAddScoped<INotificationPreferenceStore, NotificationPreferenceStore>();
        services.TryAddScoped<ITenantProviderConfigStore, TenantProviderConfigStore>();
        services.TryAddScoped<IPreferenceResolver, PreferenceResolver>();
        services.TryAddScoped<IProviderConfigResolver, ProviderConfigResolver>();
        services.TryAddScoped<INotificationDispatcher, NotificationDispatcher>();

        ContributeDapperMappings(services);
        services.AddHostedService<OutboxDrainer>();

        return services;
    }

    // Mirror Storage: scan the collection for the already-registered EntityMappingRegistry singleton
    // instance and apply the Notifications mappings to it. No service provider is built. No-op when EF is the peer.
    private static void ContributeDapperMappings(IServiceCollection services)
    {
        for (var i = services.Count - 1; i >= 0; i--)
        {
            if (services[i].ServiceType == typeof(EntityMappingRegistry)
                && services[i].ImplementationInstance is EntityMappingRegistry registry)
            {
                NotificationsDapperMappings.Apply(registry);
                return;
            }
        }
    }
}
