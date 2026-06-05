using FluentMigrator.Runner;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.MySql;
using Themia.Exceptional;
using Themia.Exceptional.Migrations;
using Themia.Exceptional.MySql;
using Xunit;

namespace Themia.Exceptional.MySql.IntegrationTests;

[Trait("Category", "Integration")]
public class MySqlExceptionStoreTests : IAsyncLifetime
{
    private readonly MySqlContainer container = new MySqlBuilder("mysql:8.4").Build();

    // No GuidFormat suffix: MySqlExceptionalDialect applies GuidFormat=Char36 itself, so a plain
    // connection string round-trips System.Guid ↔ CHAR(36) — this exercises that behavior.
    private string ConnString => container.GetConnectionString();
    private ExceptionStoreEngine Engine => new(new MySqlExceptionalDialect(ConnString));

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        using var provider = new ServiceCollection()
            .AddFluentMigratorCore()
            .ConfigureRunner(rb => rb.AddMySql8().WithGlobalConnectionString(ConnString)
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
    public async Task ListAsync_FiltersByDateRange_MySql()
    {
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
        // Proves the Local→UTC conversion works against real MySQL DATETIME(6).
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
}
