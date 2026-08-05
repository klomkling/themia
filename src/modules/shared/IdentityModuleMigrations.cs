using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Data.Migrations;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Migrations;

namespace Themia.Modules.Identity.Internal;

/// <summary>
/// Applies the Identity schema on module startup. Shared source, compiled into both engine packages.
/// </summary>
/// <remarks>
/// Linked into <c>Themia.Modules.Identity.Dapper</c> and <c>Themia.Modules.Identity.EFCore</c> rather than
/// copied: the two modules differ only in which store they register, and a connection-string lookup or
/// migration-run change made in one copy and not the other would leave the engines applying the schema
/// differently with no compiler signal. A shared base class would have to be public (CS0060) and would
/// then ship the same public type from both packages, so the shared unit is this helper instead.
/// </remarks>
internal static class IdentityModuleMigrations
{
    internal static void Run(IServiceProvider serviceProvider, MigrationEngine engine, IdentityModuleOptions options)
    {
        using var scope = serviceProvider.CreateScope();
        var configuration = scope.ServiceProvider.GetRequiredService<IConfiguration>();
        var connectionString = configuration.GetConnectionString(options.ConnectionStringName)
            ?? throw new InvalidOperationException(
                $"Connection string '{options.ConnectionStringName}' was not found; the identity module requires it.");

        ThemiaMigrations.Run(engine, connectionString, IdentityMigrations.Assembly);
    }
}
