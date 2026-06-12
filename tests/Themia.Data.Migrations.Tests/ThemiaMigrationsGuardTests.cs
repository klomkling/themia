using System.Reflection;
using Themia.Data.Migrations;
using Xunit;

namespace Themia.Data.Migrations.Tests;

public class ThemiaMigrationsGuardTests
{
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
}
