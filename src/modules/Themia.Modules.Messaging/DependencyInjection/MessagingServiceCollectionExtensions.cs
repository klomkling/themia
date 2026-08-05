using System.Linq;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

using Themia.Framework.Data.Dapper.Connection;
using Themia.Framework.Data.Dapper.Mapping;
using Themia.Messaging.DependencyInjection;
using Themia.Messaging;
using Themia.Messaging.Inbox;
using Themia.Messaging.Outbox;
using Themia.Modules.Messaging.Inbox;
using Themia.Modules.Messaging.Mapping;
using Themia.Modules.Messaging.Stores;

namespace Themia.Modules.Messaging.DependencyInjection;

/// <summary>Registers the Themia Messaging module services (outbox store, drainer, retention).</summary>
public static class MessagingServiceCollectionExtensions
{
    /// <summary>
    /// Adds the Messaging module's own services: the peer-agnostic outbox store, the
    /// <see cref="DrainSignal{TRow}"/>, and the shared <c>OutboxDrainer</c> hosted service. Requires
    /// <c>AddThemiaMessagingIdentity(...)</c> to already be registered: <c>MessageOutboxStore</c> stamps that
    /// identity on every message it originates. The adopter must ALSO register a provider dialect via
    /// <c>AddThemiaMessaging{PostgreSql|MySql|SqlServer}(...)</c>, an <see cref="IOutboxDispatcher{TRow}"/>
    /// that delivers messages, and a framework data peer (EF with <c>modelBuilder.ApplyThemiaMessaging()</c>,
    /// or Dapper); then run <c>MessagingModule.InitializeAsync</c> to apply the schema migration.
    /// </summary>
    /// <remarks>
    /// <b>If the host uses the Dapper peer, registration order matters here too.</b> Call
    /// <c>AddThemiaDapperCore()</c> (and the engine package) BEFORE this method: the Messaging entity
    /// mapping is contributed to the Dapper <c>EntityMappingRegistry</c> singleton at the moment this
    /// method runs, so calling it first leaves <c>MessageOutboxEntry</c> unmapped. If a Dapper data peer is
    /// registered but that registry is not, this method throws instead of silently mirroring the (correct)
    /// EF-peer no-op.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional callback to configure <see cref="MessagingModuleOptions"/>.</param>
    /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
    /// <exception cref="InvalidOperationException">
    /// No <see cref="MessagingIdentity"/> is registered yet — call <c>AddThemiaMessagingIdentity(...)</c>
    /// before this method. Or: a Dapper data peer is registered but its <c>EntityMappingRegistry</c> is not
    /// — call <c>AddThemiaDapperCore()</c> before this method.
    /// </exception>
    public static IServiceCollection AddThemiaMessagingModule(
        this IServiceCollection services,
        Action<MessagingModuleOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        MessagingRegistrationGuards.RequireRegistered<MessagingIdentity>(
            services,
            "AddThemiaMessagingModule requires AddThemiaMessagingIdentity(...) to already be registered: "
            + "MessageOutboxStore stamps this service's identity on every message it originates. Call "
            + "AddThemiaMessagingIdentity(...) BEFORE calling AddThemiaMessagingModule.");

        var options = new MessagingModuleOptions();
        configure?.Invoke(options);
        options.Validate();

        services.TryAddSingleton(options);
        services.TryAddSingleton(TimeProvider.System);
        services.AddLogging();

        services.TryAddSingleton<DrainSignal<ClaimedMessageRow>>();

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

        // No-op when EF is the peer; throws when a Dapper peer is present but its registry is not — the one
        // ordering that leaves MessageOutboxEntry permanently unmapped, so the first enqueue fails at
        // commit with a missing-relation error and nothing points back at the registration order.
        services.ContributeDapperMappings(MessagingDapperMappings.Apply, nameof(AddThemiaMessagingModule));
        services.AddHostedService<OutboxDrainer<ClaimedMessageRow>>();

        return services;
    }

    /// <summary>
    /// Adds the deduplicating inbox. REQUIRES the Dapper data peer: admission must commit inside the
    /// caller's transaction, and only the Dapper peer exposes an ambient connection. Throws at startup on
    /// an EF-only host rather than degrading to a non-transactional admission that could lose messages.
    /// REQUIRES <see cref="AddThemiaMessagingModule"/> to already be registered: <see cref="InboxPurgeService"/>
    /// needs its <see cref="MessagingModuleOptions"/> and <c>IOutboxDialect&lt;ClaimedMessageRow&gt;</c>.
    /// </summary>
    /// <remarks>
    /// <b>Registration order matters, for two independent reasons.</b> Call
    /// <c>AddThemiaDapper{Postgres|MySql|SqlServer}(...)</c> (and the provider's messaging dialect, e.g.
    /// <c>AddThemiaMessagingPostgreSql(...)</c>) BEFORE this method, for the Dapper peer. ALSO call
    /// <see cref="AddThemiaMessagingModule"/> BEFORE this method, for <see cref="MessagingModuleOptions"/>
    /// and the outbox dialect that <see cref="InboxPurgeService"/> resolves — without it, a receive-only host
    /// that never calls <see cref="AddThemiaMessagingModule"/> would otherwise fail at <c>IHost.StartAsync</c>
    /// with an opaque DI activation error instead of a clear message at registration time. Both checks below
    /// scan the collection built so far, so calling this method before either prerequisite throws even on a
    /// host that registers it later.
    /// </remarks>
    /// <param name="services">The service collection.</param>
    /// <returns>The same <see cref="IServiceCollection"/> for chaining.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="services"/> is null.</exception>
    /// <exception cref="InvalidOperationException">
    /// No Dapper data peer is registered yet, or <see cref="AddThemiaMessagingModule"/> has not been called yet.
    /// </exception>
    public static IServiceCollection AddThemiaMessagingInbox(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        MessagingRegistrationGuards.RequireRegistered<IDapperConnectionContext>(
            services,
            "AddThemiaMessagingInbox requires the Dapper data peer: register AddThemiaDapper{Postgres|MySql|SqlServer}(...) "
            + "BEFORE calling AddThemiaMessagingInbox. The inbox is not supported on the EF peer because admission must "
            + "commit inside the caller's transaction, and Themia.Framework.Data.EFCore exposes no ambient connection.");

        MessagingRegistrationGuards.RequireRegistered<MessagingModuleOptions>(
            services,
            "AddThemiaMessagingInbox requires AddThemiaMessagingModule(...) to already be registered: "
            + "InboxPurgeService needs the MessagingModuleOptions and outbox dialect that only "
            + "AddThemiaMessagingModule registers. Call AddThemiaMessagingModule(...) BEFORE calling "
            + "AddThemiaMessagingInbox.");

        services.TryAddScoped<IInboxStore, DapperInboxStore>();
        services.AddHostedService<InboxPurgeService>();

        return services;
    }

}
