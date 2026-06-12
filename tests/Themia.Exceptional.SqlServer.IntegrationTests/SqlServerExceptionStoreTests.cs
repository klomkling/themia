using Testcontainers.MsSql;
using Themia.Data.Migrations;
using Themia.Exceptional;
using Themia.Exceptional.Conformance;
using Themia.Exceptional.Migrations;
using Themia.Exceptional.SqlServer;
using Xunit;

namespace Themia.Exceptional.SqlServer.IntegrationTests;

[Trait("Category", "Integration")]
public class SqlServerExceptionStoreTests : ExceptionStoreConformanceTests, IAsyncLifetime
{
    // MsSqlBuilder 4.12.0: parameterless ctor is [Obsolete] as error — must pass image explicitly.
    // Pinned (not :2022-latest) for reproducible CI; this is Testcontainers.MsSql 4.12.0's default image.
    // No GuidFormat quirk — uniqueidentifier round-trips natively through Microsoft.Data.SqlClient.
    private readonly MsSqlContainer container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04").Build();

    private string ConnString => container.GetConnectionString();
    private ExceptionStoreEngine Engine => new(new SqlServerExceptionalDialect(ConnString), new ExceptionalOptions { ApplicationName = "App" });

    protected override IExceptionStore Store => Engine;

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        ThemiaMigrations.Run(MigrationEngine.SqlServer, ConnString, typeof(ExceptionLogMigration).Assembly);
    }

    public async Task DisposeAsync() => await container.DisposeAsync();

    // Engine-specific: datetime2 preserves sub-millisecond ticks — unique to SQL Server.
    [Fact]
    [Trait("Category", "Integration")]
    public async Task Insert_PreservesSubMillisecondPrecision_OnDateTime2()
    {
        var store = Engine;
        var precise = new DateTime(2026, 6, 6, 12, 0, 0, DateTimeKind.Utc).AddTicks(1234567);
        var guid = Guid.NewGuid();
        await store.LogAsync(new ExceptionEntry
        {
            Guid = guid, ApplicationName = "App", MachineName = "M", Type = "T",
            Message = "m", Detail = "d", ErrorHash = Guid.NewGuid().ToString("N"),
            DuplicateCount = 1, CreationDate = precise, LastLogDate = precise,
        });

        var read = await store.GetAsync(guid);
        Assert.NotNull(read);
        Assert.Equal(precise.Ticks % TimeSpan.TicksPerMillisecond, read!.CreationDate.Ticks % TimeSpan.TicksPerMillisecond);
    }
}
