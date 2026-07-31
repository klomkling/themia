using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Data.Migrations;
using Themia.Framework.Core.Modules;
using Themia.Modules.Notifications.Migrations;

namespace Themia.Modules.Notifications;

/// <summary>The Themia Notifications module: outbox, drainer, dispatcher, preferences, in-app, provider config.
/// <see cref="InitializeAsync"/> runs the FluentMigrator schema. The host wires the services via
/// <c>AddThemiaNotificationsModule(...)</c>; this module exists for hosts that drive modules through the
/// <see cref="IThemiaModule"/> convention.</summary>
public sealed class NotificationsModule : ThemiaModuleBase
{
    private readonly MigrationEngine engine;
    private readonly NotificationsModuleOptions options;

    /// <summary>Creates the module with default options.</summary>
    /// <param name="engine">The migration engine for the schema.</param>
    public NotificationsModule(MigrationEngine engine) : this(engine, new NotificationsModuleOptions()) { }

    /// <summary>Creates the module with explicit options.</summary>
    /// <param name="engine">The migration engine for the schema.</param>
    /// <param name="options">The module options.</param>
    public NotificationsModule(MigrationEngine engine, NotificationsModuleOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        this.engine = engine;
        this.options = options;
    }

    /// <inheritdoc />
    public override ModuleDescriptor Descriptor { get; } = new(
        name: "Themia.Notifications",
        displayName: "Notifications",
        description: "Tenant-aware notifications: transactional outbox, background drainer, multi-channel dispatcher.",
        // Bumped for the breaking DrainSignal/INotificationsSqlDialect move into Themia.Messaging — see
        // MIGRATION.md "Unreleased". 0.11.0 tracks the next planned release: this PR also adds the new
        // Themia.Messaging/Themia.Modules.Messaging module, which is a MINOR bump per the versioning
        // policy in CHANGELOG.md regardless of this breaking change on its own.
        version: new Version(0, 11, 0, 0));

    /// <inheritdoc />
    public override ValueTask InitializeAsync(IServiceProvider serviceProvider, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        cancellationToken.ThrowIfCancellationRequested();

        using var scope = serviceProvider.CreateScope();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var connectionString = configuration.GetConnectionString(options.ConnectionStringName)
            ?? throw new InvalidOperationException(
                $"Connection string '{options.ConnectionStringName}' was not found; the notifications module requires it.");

        ThemiaMigrations.Run(engine, connectionString, typeof(NotificationsSchemaMigration).Assembly);
        return ValueTask.CompletedTask;
    }
}
