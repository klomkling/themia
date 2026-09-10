using Microsoft.Extensions.DependencyInjection;

using Testcontainers.PostgreSql;

using Themia.Exceptional;

using Xunit;

namespace Themia.Exceptional.PostgreSql.IntegrationTests;

/// <summary>
/// Proves <c>AddThemiaExceptionalPostgres</c> migrates a fresh database on its own, through
/// <c>PostgresMigrationEngine.Adapter</c> directly rather than through <c>MigrationEngineRegistry</c>
/// (design spec §5.3, Task 3). <see cref="PostgresExceptionStoreTests"/> and the other classes in this
/// project build <c>ExceptionStoreEngine</c> by hand and migrate via
/// <c>ThemiaMigrations.Run(MigrationEngine.Postgres, …)</c> directly — see <c>EngineRegistration.cs</c> —
/// so none of them actually exercises this entry point. This test creates its own container (not shared
/// with any other class) so the database it migrates starts genuinely empty.
/// </summary>
[Trait("Category", "Integration")]
public sealed class PostgresExceptionalEntryPointTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:16-alpine").Build();

    public Task InitializeAsync() => container.StartAsync();

    public Task DisposeAsync() => container.DisposeAsync().AsTask();

    [Fact]
    public async Task AddThemiaExceptionalPostgres_migrates_a_fresh_database()
    {
        var services = new ServiceCollection();
        services.AddThemiaExceptionalPostgres(container.GetConnectionString(), o => o.ApplicationName = "App");

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
