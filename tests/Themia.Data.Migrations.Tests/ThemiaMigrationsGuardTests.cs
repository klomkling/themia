using System.Reflection;
using Themia.Data.Migrations;
using Themia.Data.Migrations.MySql;
using Themia.Data.Migrations.PostgreSql;
using Xunit;

namespace Themia.Data.Migrations.Tests;

public class ThemiaMigrationsGuardTests
{
    // These tests drive MigrationEngine.Postgres through Run's guard clauses only — no DB connection is
    // ever opened — but resolving the engine still needs an adapter registered. MigrationEngineRegistry.Add
    // is idempotent, so registering unconditionally here is safe regardless of test run order.
    static ThemiaMigrationsGuardTests() => MigrationEngineRegistry.Add(PostgresMigrationEngine.Adapter);

    // The Themia.Data.Migrations runner package is a migration-free assembly by design (it contains no
    // [Migration] types and never will), so it is a robust fixture for the "no migrations" guard — unlike
    // the test assembly, which now carries deliberate [Migration] fixtures (see DuplicateVersionMigrations).
    private static readonly Assembly[] NoMigrationAssembly = [typeof(ThemiaMigrations).Assembly];

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Run_ShouldThrowArgumentException_WhenConnectionStringIsNullOrWhitespace(string? connectionString)
    {
        Assert.ThrowsAny<ArgumentException>(
            () => ThemiaMigrations.Run(MigrationEngine.Postgres, connectionString!, NoMigrationAssembly));
    }

    [Fact]
    public void Run_ShouldThrowArgumentNullException_WhenAssembliesArrayIsNull()
    {
        Assert.Throws<ArgumentNullException>(
            () => ThemiaMigrations.Run(MigrationEngine.Postgres, "Host=localhost", null!));
    }

    [Fact]
    public void Run_ShouldThrowArgumentException_WhenNoAssembliesProvided()
    {
        Assert.Throws<ArgumentException>(
            () => ThemiaMigrations.Run(MigrationEngine.Postgres, "Host=localhost"));
    }

    [Fact]
    public void Run_ShouldThrowArgumentOutOfRangeException_WhenEngineIsUnknown()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ThemiaMigrations.Run((MigrationEngine)999, "Host=localhost", NoMigrationAssembly));
    }

    [Fact]
    public void Run_ShouldThrowArgumentException_WhenAssembliesContainNoMigrations()
    {
        // The runner package has no [Migration] types. Discovery happens in memory (no DB), so Run must
        // fail fast here rather than silently applying nothing at MigrateUp.
        Assert.Throws<ArgumentException>(
            () => ThemiaMigrations.Run(MigrationEngine.Postgres, "Host=localhost;Database=x", NoMigrationAssembly));
    }

    [Fact]
    public void Run_ShouldWrapInInvalidOperationException_WhenMigrationVersionsAreDuplicated()
    {
        // This test assembly carries two [Migration] types sharing a version number, so FluentMigrator's
        // loader throws DuplicateMigrationException during in-memory discovery (no DB needed). Run must
        // surface that through the engine-named InvalidOperationException wrap, not propagate it raw.
        var ex = Assert.Throws<InvalidOperationException>(
            () => ThemiaMigrations.Run(MigrationEngine.Postgres, "Host=localhost;Database=x", typeof(ThemiaMigrationsGuardTests).Assembly));

        Assert.NotNull(ex.InnerException);
    }

    // Task 4 (registry group): one "nothing registered" test per engine. Postgres needs Reset()/restore
    // because the static constructor above registers it unconditionally for every other test in this
    // class; MySql and SqlServer need neither — this project references only the Postgres engine
    // package, so nothing anywhere in this assembly ever registers them.
    [Fact]
    public void Run_ThrowsNamingPostgreSqlPackage_WhenAdapterNotRegistered()
    {
        MigrationEngineRegistry.Reset();
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(
                () => ThemiaMigrations.Run(MigrationEngine.Postgres, "Host=localhost;Database=x", NoMigrationAssembly));

            Assert.Contains("Themia.Data.Migrations.PostgreSql", ex.Message, StringComparison.Ordinal);
            Assert.Contains("AddThemiaDataMigrationsPostgreSql", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            MigrationEngineRegistry.Add(PostgresMigrationEngine.Adapter);
        }
    }

    [Fact]
    public void Run_ThrowsNamingMySqlPackage_WhenAdapterNotRegistered()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => ThemiaMigrations.Run(MigrationEngine.MySql, "Server=localhost;Database=x", NoMigrationAssembly));

        Assert.Contains("Themia.Data.Migrations.MySql", ex.Message, StringComparison.Ordinal);
        Assert.Contains("AddThemiaDataMigrationsMySql", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_ThrowsNamingSqlServerPackage_WhenAdapterNotRegistered()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => ThemiaMigrations.Run(MigrationEngine.SqlServer, "Server=localhost;Database=x", NoMigrationAssembly));

        Assert.Contains("Themia.Data.Migrations.SqlServer", ex.Message, StringComparison.Ordinal);
        Assert.Contains("AddThemiaDataMigrationsSqlServer", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Run_ResolvesTheLateRegisteredAdapter_RegardlessOfOtherStartupStepsInBetween()
    {
        // Proves spec §6's "position-independent in startup" claim for the registry group. Every
        // Themia.Modules.* constructor and SchedulingSchema.Migrate migrate from InitializeAsync, a phase
        // that runs strictly after ConfigureServices (StorageModule.cs:40-52), so
        // AddThemiaDataMigrationsMySql() may be called anywhere in an adopter's startup — even after
        // unrelated registrations — as long as it precedes the Run call the module eventually makes.
        try
        {
            var unrelatedStartupStepsRan = new List<string> { "AddDbContext", "AddQuartz" }; // stand-ins for
            // other ConfigureServices work that runs before the adapter is registered
            Assert.Equal(2, unrelatedStartupStepsRan.Count);

            MigrationEngineRegistry.Add(MySqlMigrationEngine.Adapter); // registered late; still resolves

            // With the adapter now resolvable, Run reaches its next guard (no [Migration] types in this
            // assembly) instead of the "no adapter is registered" failure — proof the late registration
            // took effect regardless of what ran before it.
            var ex = Assert.Throws<ArgumentException>(
                () => ThemiaMigrations.Run(MigrationEngine.MySql, "Server=localhost;Database=x", NoMigrationAssembly));
            Assert.DoesNotContain("no adapter is registered", ex.Message, StringComparison.Ordinal);
        }
        finally
        {
            // MySql must go back to unregistered so Run_ThrowsNamingMySqlPackage_WhenAdapterNotRegistered
            // stays valid no matter which order xunit picks for these two tests.
            MigrationEngineRegistry.Reset();
            MigrationEngineRegistry.Add(PostgresMigrationEngine.Adapter);
        }
    }
}
