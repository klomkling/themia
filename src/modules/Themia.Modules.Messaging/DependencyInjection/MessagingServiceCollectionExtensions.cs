using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Themia.Framework.Data.Dapper.Mapping;
using Themia.Messaging.Outbox;
using Themia.Modules.Messaging.Mapping;
using Themia.Modules.Messaging.Stores;

namespace Themia.Modules.Messaging.DependencyInjection;

/// <summary>Registers the Themia Messaging module services (outbox store, drainer, retention).</summary>
public static class MessagingServiceCollectionExtensions
{
    /// <summary>
    /// Adds the Messaging module's own services: the peer-agnostic outbox store, the
    /// <see cref="DrainSignal"/>, and the shared <c>OutboxDrainer</c> hosted service. The adopter must ALSO
    /// register a provider dialect via <c>AddThemiaMessaging{PostgreSql|MySql|SqlServer}(...)</c>, an
    /// <see cref="IOutboxDispatcher{TRow}"/> that delivers messages, and a framework data peer (EF with
    /// <c>modelBuilder.ApplyThemiaMessaging()</c>, or Dapper); then run
    /// <c>MessagingModule.InitializeAsync</c> to apply the schema migration.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional callback to configure <see cref="MessagingModuleOptions"/>.</param>
    /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
    public static IServiceCollection AddThemiaMessagingModule(
        this IServiceCollection services,
        Action<MessagingModuleOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = new MessagingModuleOptions();
        configure?.Invoke(options);
        options.Validate();

        services.TryAddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.AddLogging();

        services.TryAddSingleton<DrainSignal>();

        services.TryAddSingleton(new OutboxDrainerOptions<ClaimedMessageRow>
        {
            DrainIntervalSeconds = options.DrainIntervalSeconds,
            MaxBatchSize = options.MaxBatchSize,
            MaxAttempts = options.MaxAttempts,
            LeaseSeconds = options.LeaseSeconds,
            PurgeEnabled = options.PurgeEnabled,
            SentRetentionDays = options.SentRetentionDays,
            DeadRetentionDays = options.DeadRetentionDays,
        });

        services.TryAddScoped<IMessageOutboxStore, MessageOutboxStore>();

        ContributeDapperMappings(services);
        services.AddHostedService<OutboxDrainer<ClaimedMessageRow>>();

        return services;
    }

    // Mirror Notifications: scan the collection for the already-registered EntityMappingRegistry singleton
    // instance and apply the Messaging mappings to it. No service provider is built. No-op when EF is the peer.
    private static void ContributeDapperMappings(IServiceCollection services)
    {
        for (var i = services.Count - 1; i >= 0; i--)
        {
            if (services[i].ServiceType == typeof(EntityMappingRegistry)
                && services[i].ImplementationInstance is EntityMappingRegistry registry)
            {
                MessagingDapperMappings.Apply(registry);
                return;
            }
        }
    }
}
