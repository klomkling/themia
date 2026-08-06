using Dapper;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Testcontainers.MsSql;
using Testcontainers.MySql;
using Testcontainers.PostgreSql;

using Themia.Challenges.DependencyInjection;
using Themia.Challenges.Internal;
using Themia.Challenges.MySql;
using Themia.Challenges.PostgreSql;
using Themia.Challenges.SqlServer;

using Xunit;

namespace Themia.Challenges.IntegrationTests;

/// <summary>
/// One real container per engine, shared across every test class for that engine via xUnit's
/// collection-fixture mechanism (<see cref="PostgresChallengesCollection"/> and its MySQL/SQL Server
/// siblings) so the three test files in this project (store/hashing/tenant-isolation, concurrency,
/// retention) reuse a single running Postgres/MySQL/SQL Server instance instead of each starting its
/// own. Registration goes through the exact same <c>AddThemiaChallenges</c> +
/// <c>AddThemiaChallenges&lt;Engine&gt;</c> DI path an adopter uses — the latter runs
/// <see cref="Migrations.ChallengeSchemaMigration"/> as a side effect of construction, so the schema
/// exists by the time <see cref="InitializeAsync"/> returns.
/// <para>
/// Every purpose used anywhere in this project is configured once, here — no test class carries its
/// own <see cref="ChallengeOptions"/> setup. Tests avoid interfering with each other, despite sharing
/// one schema and one container, by keying every scope on a fresh <see cref="Guid"/>-derived key and
/// tenant id rather than relying on any isolation the shared tables don't provide.
/// </para>
/// </summary>
public abstract class ChallengeEngineFixture : IAsyncLifetime
{
    /// <summary>Generous default limits: schema, hashing, cross-tenant, and two-simultaneous-verify tests
    /// all issue only a handful of challenges each and must never be refused by a rate-limit tripping.</summary>
    public const string GenericPurpose = "generic";

    /// <summary>Very high per-scope/per-key limits so N concurrent <c>IssueAsync</c> calls in
    /// <c>ConcurrencyTests</c> all succeed — the point of that test is the counter, not the gate.</summary>
    public const string ConcurrencyPurpose = "concurrency-issue";

    /// <summary>The purpose <c>RetentionTests</c> and the cross-tenant rate-limit test issue against
    /// while exhausting a tight per-key ceiling. The ceiling itself is store-wide (see
    /// <see cref="ChallengeOptions.PerKeyWindow"/>), so those tests run against
    /// <see cref="CreateServiceWithTightKeyCeiling"/> rather than the shared <see cref="Service"/>.</summary>
    public const string TightPurpose = "tight-key-ceiling";

    /// <summary>The per-key ceiling <see cref="CreateServiceWithTightKeyCeiling"/> configures.</summary>
    public const int TightPerKeyLimit = 3;

    /// <summary>A purpose issuing <see cref="ChallengeFormat.OpaqueToken"/> secrets, so the magic-link
    /// path (<see cref="IChallengeService.VerifyByTokenAsync"/>) has rows it can actually resolve.</summary>
    public const string TokenPurpose = "token-link";

    /// <summary>A second opaque-token purpose, so "a token does not verify under another purpose" is
    /// testable against a purpose that is itself token-shaped rather than against a numeric one.</summary>
    public const string OtherTokenPurpose = "token-link-other";

    private ServiceProvider provider = null!;

    /// <summary>The live <see cref="IChallengeService"/>, resolved from DI exactly as an adopter would —
    /// every functional assertion in this project goes through this, never the dialect directly.</summary>
    public IChallengeService Service { get; private set; } = null!;

    /// <summary>The engine's <see cref="IChallengeDialect"/> — used only for the raw-SQL assertions no
    /// public API exposes: schema existence, reading a persisted row's actual columns, and (SQL Server
    /// only) forcing the rate-window seed collision inside an ambient transaction.</summary>
    public IChallengeDialect Dialect { get; private set; } = null!;

    /// <summary>The resolved <see cref="ChallengeOptions"/> — exposed so <c>ConcurrencyTests</c> can
    /// construct its own <c>ChallengeService</c> instances directly (bypassing DI) over a
    /// <see cref="RaceGatingChallengeDialect"/>-wrapped <see cref="Dialect"/>, mirroring
    /// <c>Themia.Challenges.Tests.ChallengeServiceTests</c>' own race test.</summary>
    public ChallengeOptions Options { get; private set; } = null!;

    /// <summary>The resolved <see cref="System.TimeProvider"/> — see <see cref="Options"/>'s remarks.</summary>
    public TimeProvider TimeProvider { get; private set; } = null!;

    /// <summary>The engine-appropriate quoting of the reserved <c>key</c> column, for raw SQL this
    /// project writes directly against the schema (e.g. <c>"key"</c> / <c>`key`</c> / <c>[key]</c>).</summary>
    public abstract string KeyColumn { get; }

    /// <summary>Starts the engine's container and returns its connection string.</summary>
    protected abstract Task<string> StartContainerAsync();

    /// <summary>Stops and disposes the engine's container.</summary>
    protected abstract Task StopContainerAsync();

    /// <summary>Registers the engine's <see cref="IChallengeDialect"/> (and runs the schema migration as
    /// a side effect — see <c>AddThemiaChallenges&lt;Engine&gt;</c>'s own remarks).</summary>
    protected abstract void RegisterDialect(IServiceCollection services, string connectionString);

    /// <summary>Every table name <see cref="Migrations.ChallengeSchemaMigration"/> actually created,
    /// read from the engine's own catalog — not assumed from the migration source.</summary>
    public abstract Task<HashSet<string>> GetTableNamesAsync();

    /// <summary>Every index name <see cref="Migrations.ChallengeSchemaMigration"/> actually created,
    /// read from the engine's own catalog — not assumed from the migration source.</summary>
    public abstract Task<HashSet<string>> GetIndexNamesAsync();

    /// <inheritdoc />
    public async Task InitializeAsync()
    {
        var connectionString = await StartContainerAsync();

        var services = new ServiceCollection();
        services.AddThemiaChallenges(ConfigurePurposes);
        RegisterDialect(services, connectionString);
        provider = services.BuildServiceProvider();

        Service = provider.GetRequiredService<IChallengeService>();
        Dialect = provider.GetRequiredService<IChallengeDialect>();
        Options = provider.GetRequiredService<ChallengeOptions>();
        TimeProvider = provider.GetRequiredService<TimeProvider>();

        // Warm-up round trip: a brand-new container's first few connections/query-plan compilations can
        // be measurably slower than steady state (observed directly against SQL Server under Docker —
        // OrbStack in particular — as an intermittent read-your-own-write hiccup on the very first
        // connection pair opened against a freshly started instance). One throwaway issue+verify here
        // absorbs that cold-start cost before any test's timing-sensitive assertions run, so a real
        // concurrency defect is never confused with container warm-up noise.
        var warmupScope = new ChallengeScope($"warmup-{Guid.NewGuid():N}", GenericPurpose, $"warmup-{Guid.NewGuid():N}");
        var warmupIssue = await Service.IssueAsync(warmupScope);
        if (warmupIssue.Secret is not null)
        {
            await Service.VerifyAsync(warmupScope, warmupIssue.Secret);
        }
    }

    /// <inheritdoc />
    public async Task DisposeAsync()
    {
        await provider.DisposeAsync();
        await StopContainerAsync();
    }

    /// <summary>
    /// A <see cref="IChallengeService"/> over this fixture's live <see cref="Dialect"/> whose per-key
    /// ceiling is <see cref="TightPerKeyLimit"/> instead of the shared service's deliberately generous
    /// one. A separate instance is needed rather than a "tight purpose" because the per-key ceiling is
    /// store-wide by design (<see cref="ChallengeOptions.PerKeyWindow"/>) — a per-purpose ceiling would
    /// bucket the same key differently per purpose and stop being a ceiling at all.
    /// </summary>
    public IChallengeService CreateServiceWithTightKeyCeiling()
    {
        var tight = new ChallengeOptions();
        ConfigurePurposes(tight);
        tight.PerKeyWindow = (TightPerKeyLimit, TimeSpan.FromHours(1));
        return new ChallengeService(Dialect, tight, TimeProvider, NullLogger<ChallengeService>.Instance);
    }

    private static void ConfigurePurposes(ChallengeOptions options)
    {
        // Generous by default: every test but the two that deliberately exhaust a ceiling issues a
        // handful — or, in ConcurrencyTests, 64 — challenges and must never be refused by a limit.
        options.PerKeyWindow = (1_000, TimeSpan.FromHours(1));
        options.ConfigurePurpose(GenericPurpose, p => p.PerScopeWindow = (1_000, TimeSpan.FromMinutes(15)));
        options.ConfigurePurpose(ConcurrencyPurpose, p => p.PerScopeWindow = (1_000, TimeSpan.FromMinutes(15)));
        options.ConfigurePurpose(TightPurpose, p => p.PerScopeWindow = (1_000, TimeSpan.FromMinutes(15)));
        options.ConfigurePurpose(TokenPurpose, p =>
        {
            p.Format = ChallengeFormat.OpaqueToken(32);
            p.PerScopeWindow = (1_000, TimeSpan.FromMinutes(15));
        });
        options.ConfigurePurpose(OtherTokenPurpose, p =>
        {
            p.Format = ChallengeFormat.OpaqueToken(32);
            p.PerScopeWindow = (1_000, TimeSpan.FromMinutes(15));
        });
    }

    /// <summary>
    /// A service whose <see cref="ChallengeOptions.TokenVerifyWindow"/> is set tight, for the one test
    /// that exercises the opt-in token-verify ceiling. Off on the shared service, as it ships.
    /// </summary>
    public IChallengeService CreateServiceWithTightTokenVerifyCeiling(int limit)
    {
        var tight = new ChallengeOptions();
        ConfigurePurposes(tight);
        tight.TokenVerifyWindow = (limit, TimeSpan.FromHours(1));
        return new ChallengeService(Dialect, tight, TimeProvider, NullLogger<ChallengeService>.Instance);
    }
}

/// <summary>PostgreSQL fixture: one <c>postgres:16-alpine</c> container shared by every PostgreSQL test
/// class in this project.</summary>
public sealed class PostgresChallengeFixture : ChallengeEngineFixture
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:16-alpine").Build();

    /// <inheritdoc />
    public override string KeyColumn => "\"key\"";

    /// <inheritdoc />
    protected override async Task<string> StartContainerAsync()
    {
        await container.StartAsync();
        return container.GetConnectionString();
    }

    /// <inheritdoc />
    protected override async Task StopContainerAsync() => await container.DisposeAsync();

    /// <inheritdoc />
    protected override void RegisterDialect(IServiceCollection services, string connectionString) =>
        services.AddThemiaChallengesPostgres(connectionString);

    /// <inheritdoc />
    public override async Task<HashSet<string>> GetTableNamesAsync()
    {
        await using var connection = Dialect.CreateConnection();
        await connection.OpenAsync();
        var names = await connection.QueryAsync<string>(
            "SELECT tablename FROM pg_tables WHERE schemaname = 'public' AND tablename IN ('challenges', 'challenge_rate_windows');");
        return names.ToHashSet();
    }

    /// <inheritdoc />
    public override async Task<HashSet<string>> GetIndexNamesAsync()
    {
        await using var connection = Dialect.CreateConnection();
        await connection.OpenAsync();
        var names = await connection.QueryAsync<string>(
            "SELECT indexname FROM pg_indexes WHERE schemaname = 'public' AND tablename IN ('challenges', 'challenge_rate_windows');");
        return names.ToHashSet();
    }
}

/// <summary>MySQL fixture: one <c>mysql:8.4</c> container shared by every MySQL test class in this project.</summary>
public sealed class MySqlChallengeFixture : ChallengeEngineFixture
{
    private readonly MySqlContainer container = new MySqlBuilder("mysql:8.4").Build();

    /// <inheritdoc />
    public override string KeyColumn => "`key`";

    /// <inheritdoc />
    protected override async Task<string> StartContainerAsync()
    {
        await container.StartAsync();
        return container.GetConnectionString();
    }

    /// <inheritdoc />
    protected override async Task StopContainerAsync() => await container.DisposeAsync();

    /// <inheritdoc />
    protected override void RegisterDialect(IServiceCollection services, string connectionString) =>
        services.AddThemiaChallengesMySql(connectionString);

    /// <inheritdoc />
    public override async Task<HashSet<string>> GetTableNamesAsync()
    {
        await using var connection = Dialect.CreateConnection();
        await connection.OpenAsync();
        var names = await connection.QueryAsync<string>(
            "SELECT table_name FROM information_schema.tables WHERE table_schema = DATABASE() AND table_name IN ('challenges', 'challenge_rate_windows');");
        return names.ToHashSet();
    }

    /// <inheritdoc />
    public override async Task<HashSet<string>> GetIndexNamesAsync()
    {
        await using var connection = Dialect.CreateConnection();
        await connection.OpenAsync();
        var names = await connection.QueryAsync<string>(
            "SELECT DISTINCT index_name FROM information_schema.statistics WHERE table_schema = DATABASE() AND table_name IN ('challenges', 'challenge_rate_windows');");
        return names.ToHashSet();
    }
}

/// <summary>SQL Server fixture: one <c>mssql/server:2022-CU14-ubuntu-22.04</c> container shared by every
/// SQL Server test class in this project.</summary>
public sealed class SqlServerChallengeFixture : ChallengeEngineFixture
{
    private readonly MsSqlContainer container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04").Build();

    /// <inheritdoc />
    public override string KeyColumn => "[key]";

    /// <inheritdoc />
    protected override async Task<string> StartContainerAsync()
    {
        await container.StartAsync();
        return container.GetConnectionString();
    }

    /// <inheritdoc />
    protected override async Task StopContainerAsync() => await container.DisposeAsync();

    /// <inheritdoc />
    protected override void RegisterDialect(IServiceCollection services, string connectionString) =>
        services.AddThemiaChallengesSqlServer(connectionString);

    /// <inheritdoc />
    public override async Task<HashSet<string>> GetTableNamesAsync()
    {
        await using var connection = Dialect.CreateConnection();
        await connection.OpenAsync();
        var names = await connection.QueryAsync<string>(
            "SELECT name FROM sys.tables WHERE name IN ('challenges', 'challenge_rate_windows');");
        return names.ToHashSet();
    }

    /// <inheritdoc />
    public override async Task<HashSet<string>> GetIndexNamesAsync()
    {
        await using var connection = Dialect.CreateConnection();
        await connection.OpenAsync();
        var names = await connection.QueryAsync<string>("""
            SELECT i.name FROM sys.indexes i
            JOIN sys.tables t ON i.object_id = t.object_id
            WHERE t.name IN ('challenges', 'challenge_rate_windows') AND i.name IS NOT NULL;
            """);
        return names.ToHashSet();
    }
}

/// <summary>xUnit collection tying every PostgreSQL test class in this project to one shared
/// <see cref="PostgresChallengeFixture"/> container instance.</summary>
[CollectionDefinition(Name)]
public sealed class PostgresChallengesCollection : ICollectionFixture<PostgresChallengeFixture>
{
    /// <summary>The collection name test classes reference via <c>[Collection(Name)]</c>.</summary>
    public const string Name = "Postgres Challenges";
}

/// <summary>xUnit collection tying every MySQL test class in this project to one shared
/// <see cref="MySqlChallengeFixture"/> container instance.</summary>
[CollectionDefinition(Name)]
public sealed class MySqlChallengesCollection : ICollectionFixture<MySqlChallengeFixture>
{
    /// <summary>The collection name test classes reference via <c>[Collection(Name)]</c>.</summary>
    public const string Name = "MySql Challenges";
}

/// <summary>xUnit collection tying every SQL Server test class in this project to one shared
/// <see cref="SqlServerChallengeFixture"/> container instance.</summary>
[CollectionDefinition(Name)]
public sealed class SqlServerChallengesCollection : ICollectionFixture<SqlServerChallengeFixture>
{
    /// <summary>The collection name test classes reference via <c>[Collection(Name)]</c>.</summary>
    public const string Name = "SqlServer Challenges";
}
