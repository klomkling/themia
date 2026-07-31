using Dapper;

using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using Testcontainers.PostgreSql;

using Themia.Data.Migrations;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.UnitOfWork;
using Themia.Framework.Data.Dapper.DependencyInjection;
using Themia.Framework.Data.Dapper.PostgreSql.DependencyInjection;
using Themia.Framework.Data.EFCore.PostgreSql;
using Themia.Messaging.Inbox;
using Themia.Messaging.PostgreSql;
using Themia.Modules.Messaging.DependencyInjection;
using Themia.Modules.Messaging.Migrations;

using Xunit;

namespace Themia.Modules.Messaging.IntegrationTests;

/// <summary>Inbox admission on the Dapper peer: dedup keying, concurrency, transactional participation
/// with the caller, DB-generated <c>received_at</c>, and the EF-only fail-fast guard.</summary>
[Trait("Category", "Integration")]
public sealed class InboxAdmissionTests : IAsyncLifetime
{
    private const string TestType = "test.message.v1";

    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private string ConnString => container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        ThemiaMigrations.Run(MigrationEngine.Postgres, ConnString, typeof(MessagingSchemaMigration).Assembly);
    }

    public async Task DisposeAsync() => await container.DisposeAsync();

    [Fact]
    public async Task First_admission_is_accepted()
    {
        await using var provider = BuildDapperProvider();
        var messageId = Guid.CreateVersion7();

        var admission = await AdmitAsync(provider, "peer-a", messageId);

        Assert.Equal(InboxAdmission.Accepted, admission);
    }

    [Fact]
    public async Task Second_admission_of_the_same_message_is_a_duplicate()
    {
        await using var provider = BuildDapperProvider();
        var messageId = Guid.CreateVersion7();

        var first = await AdmitAsync(provider, "peer-a", messageId);
        var second = await AdmitAsync(provider, "peer-a", messageId);

        Assert.Equal(InboxAdmission.Accepted, first);
        Assert.Equal(InboxAdmission.Duplicate, second);
    }

    [Fact]
    public async Task Same_message_id_from_a_different_origin_is_accepted()
    {
        await using var provider = BuildDapperProvider();
        var messageId = Guid.CreateVersion7();

        var fromPeerA = await AdmitAsync(provider, "peer-a", messageId);
        var fromPeerB = await AdmitAsync(provider, "peer-b", messageId);

        Assert.Equal(InboxAdmission.Accepted, fromPeerA);
        Assert.Equal(InboxAdmission.Accepted, fromPeerB);
    }

    [Fact]
    public async Task Concurrent_admissions_of_the_same_message_admit_exactly_once()
    {
        await using var provider = BuildDapperProvider();
        var messageId = Guid.CreateVersion7();

        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => AdmitAsync(provider, "peer-a", messageId)));

        Assert.Equal(1, results.Count(r => r == InboxAdmission.Accepted));
        Assert.Equal(7, results.Count(r => r == InboxAdmission.Duplicate));
    }

    // The load-bearing test: if admission opened its own connection instead of joining the caller's
    // ambient transaction, the insert below would commit immediately, the rollback would have nothing
    // to undo, and the second TryAdmitAsync would see the row and answer Duplicate — exactly the crash
    // window described in IInboxStore.TryAdmitAsync's remarks. Both the direct row check and the second
    // admission must agree that nothing survived the rollback.
    [Fact]
    public async Task Rolled_back_admission_can_be_admitted_again()
    {
        await using var provider = BuildDapperProvider();
        const string origin = "peer-a";
        var messageId = Guid.CreateVersion7();

        await using (var scope = provider.CreateAsyncScope())
        {
            var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();
            var store = scope.ServiceProvider.GetRequiredService<IInboxStore>();

            await using var tx = await uow.BeginTransactionAsync(CancellationToken.None);
            var firstAdmission = await store.TryAdmitAsync(origin, messageId, TestType, CancellationToken.None);
            Assert.Equal(InboxAdmission.Accepted, firstAdmission);

            await tx.RollbackAsync(CancellationToken.None);
        }

        Assert.False(await RowExistsAsync(origin, messageId));

        var secondAdmission = await AdmitAsync(provider, origin, messageId);
        Assert.Equal(InboxAdmission.Accepted, secondAdmission);
    }

    [Fact]
    public async Task Received_at_is_set_by_the_database()
    {
        await using var provider = BuildDapperProvider();
        var messageId = Guid.CreateVersion7();
        var before = DateTimeOffset.UtcNow;

        await AdmitAsync(provider, "peer-a", messageId);

        var receivedAt = await ReadReceivedAtAsync("peer-a", messageId);
        Assert.True(receivedAt >= before.AddMinutes(-1) && receivedAt <= DateTimeOffset.UtcNow.AddMinutes(1));
    }

    [Fact]
    public async Task AddThemiaMessagingInbox_throws_when_only_the_EF_peer_is_registered()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = ConnString })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddScoped<ITenantContext>(_ => new TenantContext(new TenantId("acme")));
        services.AddThemiaMessagingModule(o => o.Origin = "test-origin");
        services.AddThemiaPostgres<TestMessagingDbContext>(configuration);

        var ex = Assert.Throws<InvalidOperationException>(() => services.AddThemiaMessagingInbox());
        Assert.Contains("Dapper", ex.Message, StringComparison.Ordinal);
    }

    private ServiceProvider BuildDapperProvider()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = ConnString })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddScoped<ITenantContext>(_ => new TenantContext(new TenantId("acme")));
        services.AddThemiaMessagingModule(o => o.Origin = "test-origin");
        services.AddThemiaDapperCore();
        services.AddThemiaDapperPostgres(configuration);
        services.AddThemiaMessagingPostgreSql();
        services.AddThemiaMessagingInbox();

        return services.BuildServiceProvider();
    }

    private static async Task<InboxAdmission> AdmitAsync(ServiceProvider provider, string origin, Guid messageId)
    {
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IInboxStore>();
        return await store.TryAdmitAsync(origin, messageId, TestType, CancellationToken.None);
    }

    private async Task<bool> RowExistsAsync(string origin, Guid messageId)
    {
        await using var connection = new NpgsqlConnection(ConnString);
        await connection.OpenAsync();
        var count = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM messaging.inbox_messages WHERE origin = @origin AND message_id = @messageId",
            new { origin, messageId });
        return count > 0;
    }

    private async Task<DateTimeOffset> ReadReceivedAtAsync(string origin, Guid messageId)
    {
        await using var connection = new NpgsqlConnection(ConnString);
        await connection.OpenAsync();
        var receivedAt = await connection.ExecuteScalarAsync<DateTime>(
            "SELECT received_at FROM messaging.inbox_messages WHERE origin = @origin AND message_id = @messageId",
            new { origin, messageId });
        return new DateTimeOffset(DateTime.SpecifyKind(receivedAt, DateTimeKind.Utc));
    }
}
