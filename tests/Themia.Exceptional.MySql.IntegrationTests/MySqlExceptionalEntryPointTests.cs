using Microsoft.Extensions.DependencyInjection;

using Testcontainers.MySql;

using Themia.Exceptional;

using Xunit;

namespace Themia.Exceptional.MySql.IntegrationTests;

/// <summary>
/// Proves <c>AddThemiaExceptionalMySql</c> migrates a fresh database on its own, through
/// <c>MySqlMigrationEngine.Adapter</c> directly rather than through <c>MigrationEngineRegistry</c>
/// (design spec §5.3, Task 3). <see cref="MySqlExceptionStoreTests"/> builds <c>ExceptionStoreEngine</c> by
/// hand and migrates via <c>ThemiaMigrations.Run(MigrationEngine.MySql, …)</c> directly — see
/// <c>EngineRegistration.cs</c> — so it does not exercise this entry point. This test creates its own
/// container (not shared with any other class) so the database it migrates starts genuinely empty.
/// </summary>
[Trait("Category", "Integration")]
public sealed class MySqlExceptionalEntryPointTests : IAsyncLifetime
{
    private readonly MySqlContainer container = new MySqlBuilder("mysql:8.4").Build();

    public Task InitializeAsync() => container.StartAsync();

    public Task DisposeAsync() => container.DisposeAsync().AsTask();

    [Fact]
    public async Task AddThemiaExceptionalMySql_migrates_a_fresh_database()
    {
        var services = new ServiceCollection();
        services.AddThemiaExceptionalMySql(container.GetConnectionString(), o => o.ApplicationName = "App");

        await using var provider = services.BuildServiceProvider();
        var store = provider.GetRequiredService<IExceptionStore>();

        var entry = new ExceptionEntry
        {
            Guid = Guid.NewGuid(),
            ApplicationName = "App",
            MachineName = "M",
            Type = "System.Exception",
            Message = "entry-point-migration",
            Detail = """{"Message":"entry-point-migration"}""",
            ErrorHash = Guid.NewGuid().ToString("N"),
            DuplicateCount = 1,
            CreationDate = DateTime.UtcNow,
            LastLogDate = DateTime.UtcNow,
        };

        // Only succeeds if the "Exceptions" table already exists on THIS fresh container — proving the
        // engine-specific entry point migrated it without any registry registration in play.
        await store.LogAsync(entry);

        var loaded = await store.GetAsync(entry.Guid);
        Assert.NotNull(loaded);
        Assert.Equal("entry-point-migration", loaded!.Message);
    }
}
