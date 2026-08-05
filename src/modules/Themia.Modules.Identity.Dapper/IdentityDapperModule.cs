using Microsoft.Extensions.DependencyInjection;
using Themia.Data.Migrations;
using Themia.Framework.Core.Modules;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.DependencyInjection;
using Themia.Modules.Identity.Dapper.DependencyInjection;
using Themia.Modules.Identity.Internal;

namespace Themia.Modules.Identity.Dapper;

/// <summary>
/// Themia module that registers Identity on the Dapper data peer, wires authorization, and
/// creates/upgrades the <c>identity</c> schema on startup via FluentMigrator.
/// </summary>
/// <remarks>
/// One module per data peer since the engine split (coord #0058). A single module could only register the
/// engine-agnostic core, which on Dapper would mean entity mappings never contributed to the Dapper registry — silently, until a query failed.
/// The <see cref="MigrationEngine"/> constructor argument is the DATABASE (PostgreSQL / SQL Server) and is
/// orthogonal to the peer; it stays explicit because the data layers expose no uniform engine signal.
/// <para>
/// <b>Configure this module AFTER the Dapper peer registration.</b> <see cref="ConfigureServices"/>
/// contributes the identity mappings to the peer's <c>EntityMappingRegistry</c> and throws when that
/// registry does not exist yet — so a host that runs its module loop before
/// <c>AddThemiaDapperPostgres(configuration)</c> fails to start rather than running on unmapped tables.
/// </para>
/// </remarks>
public sealed class IdentityDapperModule : ThemiaModuleBase
{
    private readonly MigrationEngine engine;
    private readonly IdentityModuleOptions options;

    /// <summary>Creates the module for the given migration engine with default options.</summary>
    /// <param name="engine">The database engine the schema migration targets.</param>
    public IdentityDapperModule(MigrationEngine engine)
        : this(engine, new IdentityModuleOptions())
    {
    }

    /// <summary>Creates the module for the given migration engine and options.</summary>
    /// <param name="engine">The database engine the schema migration targets.</param>
    /// <param name="options">The module options.</param>
    public IdentityDapperModule(MigrationEngine engine, IdentityModuleOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.engine = engine;
        this.options = options;
    }

    /// <inheritdoc />
    public override ModuleDescriptor Descriptor { get; } = new(
        name: "Themia.Identity.Dapper",
        displayName: "Identity",
        description: "Tenant-aware user/role/claim store with argon2id hashing and ASP.NET Core authorization integration.",
        version: new Version(0, 5, 0, 0));

    /// <inheritdoc />
    public override void ConfigureServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddThemiaIdentityDapper(options);
        services.AddThemiaIdentityAuthorization();
    }

    /// <inheritdoc />
    public override ValueTask InitializeAsync(IServiceProvider serviceProvider, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        cancellationToken.ThrowIfCancellationRequested();

        IdentityModuleMigrations.Run(serviceProvider, engine, options);
        return ValueTask.CompletedTask;
    }
}
