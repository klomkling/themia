using FluentMigrator.Runner;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;
using Themia.Exceptional;
using Themia.Exceptional.Migrations;
using Themia.Exceptional.PostgreSql;
using Xunit;

namespace Themia.Exceptional.PostgreSql.IntegrationTests;

[Trait("Category", "Integration")]
public class PostgresExceptionStoreTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:16-alpine")
        .Build();

    private string ConnString => container.GetConnectionString();
    private ExceptionStoreEngine Engine => new(new PostgresExceptionalDialect(ConnString));

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        using var provider = new ServiceCollection()
            .AddFluentMigratorCore()
            .ConfigureRunner(rb => rb.AddPostgres().WithGlobalConnectionString(ConnString)
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
    public async Task Insert_Then_Get_RoundTrips()
    {
        var entry = NewEntry();
        await Engine.LogAsync(entry);

        var loaded = await Engine.GetAsync(entry.Guid);

        Assert.NotNull(loaded);
        Assert.Equal(entry.Guid, loaded!.Guid);
        Assert.Equal("""{"Message":"boom"}""", loaded.Detail);
    }

    [Fact]
    public async Task Duplicate_RollsUp_DuplicateCount()
    {
        await Engine.LogAsync(NewEntry());
        await Engine.LogAsync(NewEntry());

        var page = await Engine.ListAsync(new ExceptionFilter());

        Assert.Single(page.Items);
        Assert.Equal(2, page.Items[0].DuplicateCount);
    }

    [Fact]
    public async Task SoftDelete_HidesFromDefaultListButCountsWithIncludeDeleted()
    {
        var entry = NewEntry("del");
        await Engine.LogAsync(entry);

        Assert.True(await Engine.DeleteAsync(entry.Guid));
        Assert.Equal(0, await Engine.CountAsync(new ExceptionFilter { ApplicationName = "App" }));
        Assert.True(await Engine.CountAsync(new ExceptionFilter { IncludeDeleted = true }) >= 1);
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
}
