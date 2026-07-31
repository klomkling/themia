using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Data.Migrations;
using Themia.Framework.Core.Modules;
using Themia.Modules.Messaging.Migrations;

namespace Themia.Modules.Messaging;

/// <summary>The Themia Messaging module: transactional outbox, background drainer, deduplicating inbox.
/// <see cref="InitializeAsync"/> runs the FluentMigrator schema. The host wires the services via
/// <c>AddThemiaMessagingModule(...)</c>; this module exists for hosts that drive modules through the
/// <see cref="IThemiaModule"/> convention.</summary>
public sealed class MessagingModule : ThemiaModuleBase
{
    private readonly MigrationEngine engine;
    private readonly MessagingModuleOptions options;

    /// <summary>Creates the module with explicit options.</summary>
    /// <param name="engine">The migration engine for the schema.</param>
    /// <param name="options">The module options.</param>
    public MessagingModule(MigrationEngine engine, MessagingModuleOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        this.engine = engine;
        this.options = options;
    }

    /// <inheritdoc />
    public override ModuleDescriptor Descriptor { get; } = new(
        name: "Themia.Messaging",
        displayName: "Messaging",
        description: "Tenant-aware inter-service messaging: transactional outbox, background drainer, deduplicating inbox.",
        version: new Version(0, 1, 0, 0));

    /// <inheritdoc />
    public override ValueTask InitializeAsync(IServiceProvider serviceProvider, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        cancellationToken.ThrowIfCancellationRequested();

        using var scope = serviceProvider.CreateScope();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var connectionString = configuration.GetConnectionString(options.ConnectionStringName)
            ?? throw new InvalidOperationException(
                $"Connection string '{options.ConnectionStringName}' was not found; the messaging module requires it.");

        ThemiaMigrations.Run(engine, connectionString, typeof(MessagingSchemaMigration).Assembly);
        return ValueTask.CompletedTask;
    }
}
