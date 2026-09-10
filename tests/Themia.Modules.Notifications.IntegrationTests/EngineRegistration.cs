using System.Runtime.CompilerServices;
using Themia.Data.Migrations;
using Themia.Data.Migrations.MySql;
using Themia.Data.Migrations.PostgreSql;
using Themia.Data.Migrations.SqlServer;

namespace Themia.Modules.Notifications.IntegrationTests;

/// <summary>
/// Registers every migration engine adapter this test project's fixtures exercise. A module initializer
/// is the right tool in a test assembly (unlike in a shipped Themia package — see
/// <see cref="MigrationEngineRegistry"/>'s remarks): this assembly is always loaded, so there is no
/// "referenced but never used" risk, and it removes any dependency on test discovery/parallelization
/// order across the fixtures that each need a registered adapter.
/// </summary>
internal static class EngineRegistration
{
    // CA2255 assumes ModuleInitializer is an application-only pattern; here it stands in for the explicit
    // AddThemiaDataMigrations{Engine}() call an adopter would make in Program.cs, scoped to a test
    // assembly that is always loaded — see the class remarks.
#pragma warning disable CA2255
    [ModuleInitializer]
    internal static void Register()
    {
        MigrationEngineRegistry.Add(PostgresMigrationEngine.Adapter);
        MigrationEngineRegistry.Add(SqlServerMigrationEngine.Adapter);
        MigrationEngineRegistry.Add(MySqlMigrationEngine.Adapter);
    }
#pragma warning restore CA2255
}
