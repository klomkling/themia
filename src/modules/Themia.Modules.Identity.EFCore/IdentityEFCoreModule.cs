using Microsoft.Extensions.DependencyInjection;
using Themia.Data.Migrations;
using Themia.Framework.Core.Modules;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.DependencyInjection;
using Themia.Modules.Identity.EFCore.DependencyInjection;
using Themia.Modules.Identity.Internal;

namespace Themia.Modules.Identity.EFCore;

/// <summary>
/// Themia module that registers Identity on the EFCore data peer, wires authorization, and
/// creates/upgrades the <c>identity</c> schema on startup via FluentMigrator.
/// </summary>
/// <remarks>
/// One module per data peer since the engine split (coord #0058). A single module could only register the
/// engine-agnostic core, which on EFCore would mean no signal that the ModelBuilder configuration still has to be applied — silently, until a query failed.
/// The <see cref="MigrationEngine"/> constructor argument is the DATABASE (PostgreSQL / SQL Server) and is
/// orthogonal to the peer; it stays explicit because the data layers expose no uniform engine signal.
/// </remarks>
public sealed class IdentityEFCoreModule : ThemiaModuleBase
{
    private readonly MigrationEngine engine;
    private readonly IdentityModuleOptions options;

    /// <summary>Creates the module for the given migration engine with default options.</summary>
    /// <param name="engine">The database engine the schema migration targets.</param>
    public IdentityEFCoreModule(MigrationEngine engine)
        : this(engine, new IdentityModuleOptions())
    {
    }

    /// <summary>Creates the module for the given migration engine and options.</summary>
    /// <param name="engine">The database engine the schema migration targets.</param>
    /// <param name="options">The module options.</param>
    public IdentityEFCoreModule(MigrationEngine engine, IdentityModuleOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.engine = engine;
        this.options = options;
    }

    /// <inheritdoc />
    public override ModuleDescriptor Descriptor { get; } = new(
        name: "Themia.Identity.EFCore",
        displayName: "Identity",
        description: "Tenant-aware user/role/claim store with argon2id hashing and ASP.NET Core authorization integration.",
        version: new Version(0, 5, 0, 0));

    /// <inheritdoc />
    public override void ConfigureServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddThemiaIdentityEFCore(options);
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
