using Microsoft.AspNetCore.DataProtection;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.DependencyInjection;
using MySqlConnector;
using Npgsql;
using Testcontainers.MsSql;
using Testcontainers.MySql;
using Testcontainers.PostgreSql;
using Themia.AspNetCore.DataProtection.MySql;
using Themia.AspNetCore.DataProtection.PostgreSql;
using Themia.AspNetCore.DataProtection.SqlServer;
using Xunit;

namespace Themia.AspNetCore.DataProtection.IntegrationTests;

/// <summary>
/// Proves the key ring is genuinely shared across instances on every engine. This is the failure the store
/// exists to prevent: with per-instance filesystem keys, the moment a request lands on a different instance
/// than the one that issued a cookie, unprotect fails.
/// </summary>
public abstract class DataProtectionKeyStoreTestsBase
{
    private const string ApplicationName = "themia-dp-test";

    protected abstract string ConnectionString { get; }

    /// <summary>Registers the engine's key store on a Data Protection builder.</summary>
    protected abstract IDataProtectionBuilder PersistKeys(IDataProtectionBuilder builder);

    /// <summary>Counts rows in <c>data_protection_keys</c> — also proves the migration created the table.</summary>
    protected abstract Task<long> CountKeysAsync();

    [Fact]
    public async Task ProtectedPayload_ShouldUnprotect_OnAnIndependentInstance()
    {
        const string Payload = "auth-cookie-payload";

        // Two fully independent containers, exactly as two application instances would be.
        await using var instance1 = BuildInstance();
        var protectedPayload = Protector(instance1).Protect(Payload);

        // The key reached OUR table rather than a per-instance key ring somewhere else. Without this the test
        // could pass on ASP.NET's default storage, which two providers in one process may happen to share.
        Assert.True(await CountKeysAsync() > 0);

        await using var instance2 = BuildInstance();
        Assert.Equal(Payload, Protector(instance2).Unprotect(protectedPayload));
    }

    [Fact]
    public async Task Registration_ShouldBeIdempotent_WhenASecondInstanceStarts()
    {
        // Every instance runs the migration at boot; the second must adopt the existing table rather than
        // fail on it, and must not mint a second key ring.
        await using var instance1 = BuildInstance();
        Protector(instance1).Protect("first");
        var afterFirst = await CountKeysAsync();

        await using var instance2 = BuildInstance();
        Protector(instance2).Protect("second");

        // The second instance reused instance 1's key rather than generating its own.
        Assert.Equal(afterFirst, await CountKeysAsync());
    }

    private ServiceProvider BuildInstance()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        // SetApplicationName stays with the application, as it would in a real host — it is the discriminator
        // that keeps two applications sharing one table from reading each other's keys.
        PersistKeys(services.AddDataProtection().SetApplicationName(ApplicationName));
        return services.BuildServiceProvider();
    }

    private static IDataProtector Protector(IServiceProvider provider) =>
        provider.GetRequiredService<IDataProtectionProvider>().CreateProtector("themia-tests");
}

[Trait("Category", "Integration")]
public class DataProtectionKeyStorePostgresTests : DataProtectionKeyStoreTestsBase, IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:16-alpine").Build();

    protected override string ConnectionString => container.GetConnectionString();

    protected override IDataProtectionBuilder PersistKeys(IDataProtectionBuilder builder) =>
        builder.PersistKeysToThemiaPostgres(ConnectionString);

    protected override async Task<long> CountKeysAsync()
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM data_protection_keys";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    public async Task InitializeAsync() => await container.StartAsync();

    public async Task DisposeAsync() => await container.DisposeAsync();
}

[Trait("Category", "Integration")]
public class DataProtectionKeyStoreMySqlTests : DataProtectionKeyStoreTestsBase, IAsyncLifetime
{
    private readonly MySqlContainer container = new MySqlBuilder("mysql:8.4").Build();

    protected override string ConnectionString => container.GetConnectionString();

    protected override IDataProtectionBuilder PersistKeys(IDataProtectionBuilder builder) =>
        builder.PersistKeysToThemiaMySql(ConnectionString);

    protected override async Task<long> CountKeysAsync()
    {
        await using var connection = new MySqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM data_protection_keys";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    public async Task InitializeAsync() => await container.StartAsync();

    public async Task DisposeAsync() => await container.DisposeAsync();
}

[Trait("Category", "Integration")]
public class DataProtectionKeyStoreSqlServerTests : DataProtectionKeyStoreTestsBase, IAsyncLifetime
{
    private readonly MsSqlContainer container =
        new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-CU14-ubuntu-22.04").Build();

    protected override string ConnectionString => container.GetConnectionString();

    protected override IDataProtectionBuilder PersistKeys(IDataProtectionBuilder builder) =>
        builder.PersistKeysToThemiaSqlServer(ConnectionString);

    protected override async Task<long> CountKeysAsync()
    {
        await using var connection = new SqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM [data_protection_keys]";
        return Convert.ToInt64(await command.ExecuteScalarAsync());
    }

    public async Task InitializeAsync() => await container.StartAsync();

    public async Task DisposeAsync() => await container.DisposeAsync();
}
