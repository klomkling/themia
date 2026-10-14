using System.Runtime.CompilerServices;
using Themia.Data.Migrations;
using Themia.Data.Migrations.PostgreSql;

namespace Themia.ETax.Data.IntegrationTests;

/// <summary>Registers the PostgreSQL migration adapter for the tests; stands in for the app's own <c>AddThemiaDataMigrationsPostgreSql()</c> call.</summary>
internal static class EngineRegistration
{
#pragma warning disable CA2255
    [ModuleInitializer]
    internal static void Register() => MigrationEngineRegistry.Add(PostgresMigrationEngine.Adapter);
#pragma warning restore CA2255
}
