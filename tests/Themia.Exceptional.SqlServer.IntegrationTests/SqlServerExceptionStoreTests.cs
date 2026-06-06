using FluentMigrator.Runner;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MsSql;
using Themia.Exceptional;
using Themia.Exceptional.Migrations;
using Themia.Exceptional.SqlServer;
using Xunit;

namespace Themia.Exceptional.SqlServer.IntegrationTests;

[Trait("Category", "Integration")]
public class SqlServerExceptionStoreTests : IAsyncLifetime
{
    // MsSqlBuilder 4.12.0: parameterless ctor is [Obsolete] as error — must pass image explicitly.
    // Pinned (not :2022-latest) for reproducible CI; this is Testcontainers.MsSql 4.12.0's default image.
    // No GuidFormat quirk — uniqueidentifier round-trips natively through Microsoft.Data.SqlClient.
    private readonly MsSqlContainer container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04").Build();

    private string ConnString => container.GetConnectionString();
    private ExceptionStoreEngine Engine => new(new SqlServerExceptionalDialect(ConnString), new ExceptionalOptions { ApplicationName = "App" });

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        using var provider = new ServiceCollection()
            .AddFluentMigratorCore()
            .ConfigureRunner(rb => rb.AddSqlServer().WithGlobalConnectionString(ConnString)
                .ScanIn(typeof(ExceptionLogMigration).Assembly).For.Migrations())
            .BuildServiceProvider(false);
        using var scope = provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<IMigrationRunner>().MigrateUp();
    }

    public async Task DisposeAsync() => await container.DisposeAsync();

    private static ExceptionEntry NewEntry(string hash = "h1")
    {
        var now = DateTime.UtcNow;
        return new ExceptionEntry
        {
            Guid = Guid.NewGuid(), ApplicationName = "App", MachineName = "M", Type = "System.Exception",
            Message = "boom", Detail = """{"Message":"boom"}""", ErrorHash = hash, DuplicateCount = 1,
            CreationDate = now, LastLogDate = now,
        };
    }

    [Fact]
    public async Task Log_And_Get_RoundTrip()
    {
        var entry = NewEntry();
        await Engine.LogAsync(entry);

        var loaded = await Engine.GetAsync(entry.Guid);

        Assert.NotNull(loaded);
        Assert.Equal(entry.Guid, loaded!.Guid);
        Assert.Equal("App", loaded.ApplicationName);
        Assert.Equal("boom", loaded.Message);
    }

    [Fact]
    public async Task Log_Duplicate_Rollups()
    {
        var engine = Engine;
        var e1 = NewEntry("dup");
        await engine.LogAsync(e1);
        var e2 = NewEntry("dup");
        e2.ApplicationName = "App";
        await engine.LogAsync(e2);

        var loaded = await engine.GetAsync(e1.Guid);

        Assert.NotNull(loaded);
        Assert.Equal(2, loaded!.DuplicateCount);

        // Rollup must increment in place, not split into a second row.
        var page = await engine.ListAsync(new ExceptionFilter());
        Assert.Single(page.Items);
    }

    [Fact]
    public async Task SoftDelete_HidesFromList()
    {
        var entry = NewEntry("del");
        await Engine.LogAsync(entry);
        await Engine.DeleteAsync(entry.Guid);

        var page = await Engine.ListAsync(new ExceptionFilter());

        Assert.DoesNotContain(page.Items, i => i.Guid == entry.Guid);
    }

    [Fact]
    public async Task SoftDelete_VisibleWithIncludeDeleted()
    {
        var entry = NewEntry("del2");
        await Engine.LogAsync(entry);
        await Engine.DeleteAsync(entry.Guid);

        var page = await Engine.ListAsync(new ExceptionFilter { IncludeDeleted = true });

        Assert.Contains(page.Items, i => i.Guid == entry.Guid);
    }

    [Fact]
    public async Task CountAsync_ReturnsCorrectCount()
    {
        var engine = Engine;
        var e = NewEntry("cnt");
        await engine.LogAsync(e);

        Assert.True(await engine.CountAsync(new ExceptionFilter { ApplicationName = "App" }) >= 1);
        Assert.True(await engine.CountAsync(new ExceptionFilter { IncludeDeleted = true }) >= 1);
    }

    [Fact]
    public async Task Protect_PreventsPurge()
    {
        var entry = NewEntry("prot");
        entry.CreationDate = DateTime.UtcNow.AddDays(-30);
        await Engine.LogAsync(entry);
        await Engine.ProtectAsync(entry.Guid);

        var removed = await Engine.PurgeAsync(DateTime.UtcNow.AddDays(-1));

        Assert.Equal(0, removed);
        Assert.NotNull(await Engine.GetAsync(entry.Guid));
    }

    [Fact]
    public async Task HardDelete_RemovesRow()
    {
        var entry = NewEntry("hard");
        await Engine.LogAsync(entry);

        Assert.True(await Engine.HardDeleteAsync(entry.Guid));
        Assert.Null(await Engine.GetAsync(entry.Guid));
    }

    [Fact]
    public async Task ListAsync_FiltersByDateRange_SqlServer()
    {
        // Proves datetime2 temporal filtering works against real SQL Server.
        var engine = Engine;
        var oldE = NewEntry("range-old");
        oldE.CreationDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        oldE.LastLogDate = oldE.CreationDate;
        var midE = NewEntry("range-mid");
        midE.CreationDate = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        midE.LastLogDate = midE.CreationDate;
        await engine.LogAsync(oldE);
        await engine.LogAsync(midE);

        var page = await engine.ListAsync(new ExceptionFilter { From = new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc) });

        Assert.Single(page.Items);
        Assert.Equal("range-mid", page.Items[0].ErrorHash);
    }

    [Fact]
    public async Task ListAsync_FiltersByDateRange_KindUnspecified_DoesNotThrow()
    {
        var engine = Engine;
        var entry = NewEntry("unspec");
        entry.CreationDate = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        entry.LastLogDate = entry.CreationDate;
        await engine.LogAsync(entry);

        // Kind=Unspecified should be coerced to Utc by AsUtc() — must not throw.
        var page = await engine.ListAsync(new ExceptionFilter { From = new DateTime(2026, 5, 1) });

        Assert.Single(page.Items);
    }

    [Fact]
    public async Task Insert_Then_Get_RoundTrips_WithRequestBody()
    {
        var engine = Engine;
        var entry = NewEntry("body");
        entry.RequestBody = "{\"key\":\"value\"}";
        await engine.LogAsync(entry);

        var loaded = await engine.GetAsync(entry.Guid);

        Assert.NotNull(loaded);
        Assert.Equal("{\"key\":\"value\"}", loaded!.RequestBody);
    }

    [Fact]
    public async Task ListAsync_FiltersByDateRange_KindLocal_DoesNotThrow_AndReturnsRow()
    {
        // Proves the Local→UTC conversion works against real SQL Server datetime2.
        var engine = Engine;
        var knownUtc = new DateTime(2026, 6, 5, 12, 0, 0, DateTimeKind.Utc);
        var entry = NewEntry("local-kind");
        entry.CreationDate = knownUtc;
        entry.LastLogDate = knownUtc;
        await engine.LogAsync(entry);

        // Build a Local-kind From that is earlier than the entry's CreationDate.
        var localFrom = DateTime.SpecifyKind(knownUtc.AddHours(-1).ToLocalTime(), DateTimeKind.Local);
        var page = await engine.ListAsync(new ExceptionFilter { From = localFrom });

        Assert.True(page.Items.Count >= 1);
        Assert.Contains(page.Items, i => i.ErrorHash == "local-kind");
    }

    [Fact]
    public async Task GetAsync_ReturnsUtcKindTimestamps()
    {
        // Proves NormalizeKinds relabels SQL Server datetime2 reads to Utc without shifting the instant.
        var engine = Engine;
        var knownUtc = new DateTime(2026, 6, 5, 12, 0, 0, DateTimeKind.Utc);
        var entry = NewEntry("utc-kind");
        entry.CreationDate = knownUtc;
        entry.LastLogDate = knownUtc;
        await engine.LogAsync(entry);

        var loaded = await engine.GetAsync(entry.Guid);

        Assert.NotNull(loaded);
        Assert.Equal(DateTimeKind.Utc, loaded!.CreationDate.Kind);
        Assert.Equal(DateTimeKind.Utc, loaded.LastLogDate.Kind);
        Assert.True((loaded.CreationDate - knownUtc).Duration() < TimeSpan.FromSeconds(1));
        Assert.True((loaded.LastLogDate - knownUtc).Duration() < TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task LogAsync_DuplicateGuid_ViolatesUniqueIndex()
    {
        // Rollup keys on ErrorHash; a different hash forces an INSERT, so the same Guid must
        // collide with the unique IX_Exceptions_Guid index.
        var engine = Engine;
        var first = NewEntry("guid-uniq-1");
        await engine.LogAsync(first);

        var duplicate = NewEntry("guid-uniq-2");
        duplicate.Guid = first.Guid;

        await Assert.ThrowsAnyAsync<System.Data.Common.DbException>(() => engine.LogAsync(duplicate));
    }

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
