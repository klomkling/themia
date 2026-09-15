using System.Runtime.CompilerServices;
using Themia.Data.Migrations;
using Themia.Data.Migrations.MySql;
using Themia.Data.Migrations.PostgreSql;
using Themia.Data.Migrations.SqlServer;

namespace Themia.Content.IntegrationTests;

/// <summary>Registers the migration engine adapters this assembly's fixtures use. A module initializer is right in a
/// test assembly, which is always loaded; see <c>Themia.Challenges.IntegrationTests.EngineRegistration</c>.</summary>
internal static class EngineRegistration
{
#pragma warning disable CA2255
    [ModuleInitializer]
    internal static void Register()
    {
        MigrationEngineRegistry.Add(PostgresMigrationEngine.Adapter);
        MigrationEngineRegistry.Add(MySqlMigrationEngine.Adapter);
        MigrationEngineRegistry.Add(SqlServerMigrationEngine.Adapter);
    }
#pragma warning restore CA2255
}
