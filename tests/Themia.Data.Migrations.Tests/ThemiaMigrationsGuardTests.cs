using System.Reflection;
using Themia.Data.Migrations;
using Xunit;

namespace Themia.Data.Migrations.Tests;

public class ThemiaMigrationsGuardTests
{
    private static readonly Assembly[] OneAssembly = [typeof(ThemiaMigrationsGuardTests).Assembly];

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Run_ShouldThrowArgumentException_WhenConnectionStringIsNullOrWhitespace(string? connectionString)
    {
        Assert.ThrowsAny<ArgumentException>(
            () => ThemiaMigrations.Run(MigrationEngine.Postgres, connectionString!, OneAssembly));
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
            () => ThemiaMigrations.Run((MigrationEngine)999, "Host=localhost", OneAssembly));
    }
}
