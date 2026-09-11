using System;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Themia.Audit.PostgreSql;
using Themia.Data.Migrations;
using Xunit;

namespace Themia.Audit.Tests;

/// <summary>
/// coord #0126: an engine-specific registration call must leave <c>MigrationEngineRegistry</c> able to
/// resolve its engine, so an adopter who never calls <c>AddThemiaDataMigrations{Engine}()</c> can still
/// take the ENUM path — <c>SchedulingSchema.Migrate(MigrationEngine.Postgres, …)</c>, any
/// <c>Themia.Modules.*</c> constructor — without a boot-time "no adapter is registered".
/// </summary>
/// <remarks>
/// This assembly is the failing adopter's shape on purpose: it references an engine package (so
/// <c>THEMIA2001</c> stays silent — that guard checks for the PACKAGE, and MSBuild cannot see whether any
/// C# calls <c>AddThemiaDataMigrationsPostgreSql()</c>) and registers no adapter of its own — no module
/// initializer, no <c>AddThemiaDataMigrations</c> call anywhere in it. ezy-assets landed exactly here on
/// 0.25.0: engine package present, registry empty, crash loop on the first enum-path migration.
/// <para>
/// <c>AddThemiaAuditPostgreSql</c> is the entry point under test because it does no I/O — the same
/// register-on-use line is in <c>AddThemiaExceptional{Engine}</c>, <c>AddThemiaChallenges{Engine}</c> and
/// <c>PersistKeysToThemia{Engine}</c>, which all migrate on the spot and so need a live database to reach.
/// </para>
/// </remarks>
public class EngineRegistryHandoffTests
{
    // The runner package contains no [Migration] types by design, so Run reaches its "no migrations"
    // guard without opening a connection — leaving engine RESOLUTION as the only thing under test here.
    private static readonly Assembly[] NoMigrationAssembly = [typeof(ThemiaMigrations).Assembly];

    [Fact]
    public void AddThemiaAuditPostgreSql_LeavesTheEnumPathResolvable_WithoutAddThemiaDataMigrationsPostgreSql()
    {
        new ServiceCollection().AddThemiaAuditPostgreSql();

        // Must fail on "this assembly has no migrations" (ArgumentException), not on resolving the
        // engine. Before the fix this threw InvalidOperationException naming Themia.Data.Migrations
        // .PostgreSql and the AddThemiaDataMigrationsPostgreSql() call — the boot-time crash reported.
        var ex = Assert.Throws<ArgumentException>(
            () => ThemiaMigrations.Run(MigrationEngine.Postgres, "Host=localhost;Database=x", NoMigrationAssembly));

        Assert.DoesNotContain("no adapter is registered", ex.Message, StringComparison.Ordinal);
    }
}
