using System.Runtime.CompilerServices;
using Themia.Data.Migrations.MySql;
using Themia.Data.Migrations.PostgreSql;
using Themia.Data.Migrations.SqlServer;

namespace Themia.Data.Migrations.IntegrationTests;

/// <summary>
/// Registers every engine adapter this test assembly exercises. A module initializer is the right tool
/// here (unlike in a shipped Themia package — see <see cref="MigrationEngineRegistry"/>'s remarks): this
/// runs in the test assembly itself, which is always loaded, so there is no "referenced but never used"
/// risk, and it removes any dependency on test discovery/parallelization order across the several test
/// classes in this project that each need a registered adapter.
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
        MigrationEngineRegistry.Add(MySqlMigrationEngine.Adapter);
        MigrationEngineRegistry.Add(SqlServerMigrationEngine.Adapter);
    }
#pragma warning restore CA2255
}
