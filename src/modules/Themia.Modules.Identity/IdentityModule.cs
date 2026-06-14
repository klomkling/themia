using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Data.Migrations;
using Themia.Framework.Core.Modules;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.DependencyInjection;
using Themia.Modules.Identity.Migrations;

namespace Themia.Modules.Identity;

/// <summary>
/// Themia module that registers the Identity services + authorization integration and creates/upgrades the
/// <c>identity</c> schema on startup via FluentMigrator. Runs on either data peer (EF or Dapper) — the engine
/// is supplied explicitly because the data layers expose no uniform engine signal.
/// </summary>
public sealed class IdentityModule : ThemiaModuleBase
{
    private readonly MigrationEngine engine;
    private readonly IdentityModuleOptions options;

    /// <summary>Creates the module for the given migration engine with default options.</summary>
    /// <param name="engine">The database engine the schema migration targets.</param>
    public IdentityModule(MigrationEngine engine)
        : this(engine, new IdentityModuleOptions())
    {
    }

    /// <summary>Creates the module for the given migration engine and options.</summary>
    /// <param name="engine">The database engine the schema migration targets.</param>
    /// <param name="options">The module options.</param>
    public IdentityModule(MigrationEngine engine, IdentityModuleOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        this.engine = engine;
        this.options = options;
    }

    /// <inheritdoc />
    public override ModuleDescriptor Descriptor { get; } = new(
        name: "Themia.Identity",
        displayName: "Identity",
        description: "Tenant-aware user/role/claim store with argon2id hashing and ASP.NET Core authorization integration.",
        version: new Version(0, 5, 0, 0));

    /// <inheritdoc />
    public override void ConfigureServices(IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddThemiaIdentityServices(o =>
        {
            o.ConnectionStringName = options.ConnectionStringName;
            o.MaxFailedAccessAttempts = options.MaxFailedAccessAttempts;
            o.LockoutDuration = options.LockoutDuration;
            o.AllowPlatformLogin = options.AllowPlatformLogin;
            o.DefaultTokenLifetime = options.DefaultTokenLifetime;
        });
        services.AddThemiaIdentityAuthorization();
    }

    /// <inheritdoc />
    public override ValueTask InitializeAsync(IServiceProvider serviceProvider, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        cancellationToken.ThrowIfCancellationRequested();

        using var scope = serviceProvider.CreateScope();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var connectionString = configuration.GetConnectionString(options.ConnectionStringName)
            ?? throw new InvalidOperationException(
                $"Connection string '{options.ConnectionStringName}' was not found; the identity module requires it.");

        ThemiaMigrations.Run(engine, connectionString, typeof(IdentitySchemaMigration).Assembly);
        return ValueTask.CompletedTask;
    }
}
