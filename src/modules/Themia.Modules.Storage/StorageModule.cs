using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Data.Migrations;
using Themia.Framework.Core.Modules;
using Themia.Modules.Storage.Migrations;

namespace Themia.Modules.Storage;

/// <summary>The <see cref="IThemiaModule"/> for Storage. <see cref="InitializeAsync"/> runs the
/// FluentMigrator schema. The host wires the service + backend via
/// <c>AddThemiaStorage(...).UseLocal/UseS3/UseR2(...)</c> (the tested entry point); this module exists
/// for hosts that drive modules through the <see cref="IThemiaModule"/> convention.</summary>
public sealed class StorageModule : ThemiaModuleBase
{
    private readonly MigrationEngine engine;
    private readonly StorageModuleOptions options;

    /// <summary>Creates the module.</summary>
    /// <param name="engine">The migration engine for the schema.</param>
    public StorageModule(MigrationEngine engine) : this(engine, new StorageModuleOptions()) { }

    /// <summary>Creates the module with explicit options.</summary>
    /// <param name="engine">The migration engine for the schema.</param>
    /// <param name="options">The module options.</param>
    public StorageModule(MigrationEngine engine, StorageModuleOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.engine = engine;
        this.options = options;
    }

    /// <inheritdoc />
    public override ModuleDescriptor Descriptor { get; } = new(
        name: "Themia.Storage",
        displayName: "Storage",
        description: "Tenant-aware object storage over Local/S3/R2 with DB-backed metadata and per-tenant quota.",
        version: new Version(0, 5, 3, 0));

    /// <inheritdoc />
    public override ValueTask InitializeAsync(IServiceProvider serviceProvider, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        cancellationToken.ThrowIfCancellationRequested();

        using var scope = serviceProvider.CreateScope();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var connectionString = configuration.GetConnectionString(options.ConnectionStringName)
            ?? throw new InvalidOperationException(
                $"Connection string '{options.ConnectionStringName}' was not found; the storage module requires it.");

        ThemiaMigrations.Run(engine, connectionString, typeof(StorageSchemaMigration).Assembly);
        return ValueTask.CompletedTask;
    }
}
