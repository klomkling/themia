using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

using Npgsql;

using Testcontainers.PostgreSql;

using Themia.Data.Migrations;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.UnitOfWork;
using Themia.Framework.Data.EFCore.Extensions;
using Themia.Framework.Data.EFCore.PostgreSql;
using Themia.Modules.Notifications.Entities;
using Themia.Modules.Notifications.Migrations;
using Themia.Modules.Notifications.Outbox;
using Themia.Modules.Notifications.PostgreSql;
using Themia.Notifications;

using Xunit;

namespace Themia.Modules.Notifications.IntegrationTests;

/// <summary>End-to-end outbox drain: enqueue → claim → dispatch → mark sent / dead (EF peer, Postgres).</summary>
[Trait("Category", "Integration")]
public sealed class OutboxRoundTripTests : IAsyncLifetime
{
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:16-alpine").Build();
    private string ConnString => container.GetConnectionString();

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        ThemiaMigrations.Run(MigrationEngine.Postgres, ConnString, typeof(NotificationsSchemaMigration).Assembly);
    }

    public async Task DisposeAsync() => await container.DisposeAsync();

    [Fact]
    public async Task Drain_sends_a_pending_email_and_marks_it_sent()
    {
        var recorder = new RecordingEmailSender(succeed: true);
        await using var provider = BuildProvider(recorder);

        var id = await EnqueueEmailAsync(provider, "to@example.com", "Hi", "Body");

        var drainer = CreateDrainer(provider);
        var drained = await DrainAsync(drainer);

        Assert.Equal(1, drained);
        Assert.Single(recorder.Sent);
        Assert.Equal("to@example.com", recorder.Sent[0].Recipient);

        var (status, attempts, _) = await ReadRowAsync(id);
        Assert.Equal((int)OutboxStatus.Sent, status); // 2
        Assert.Equal(0, attempts);
    }

    [Fact]
    public async Task Failing_sender_retries_then_dead_letters_after_max_attempts()
    {
        var recorder = new RecordingEmailSender(succeed: false);
        await using var provider = BuildProvider(recorder);

        var id = await EnqueueEmailAsync(provider, "to@example.com", "Hi", "Body");

        var drainer = CreateDrainer(provider);

        // First failure: row goes to `failed` (3) and the retry time moves into the future.
        var beforeFirst = DateTimeOffset.UtcNow;
        await ForceClaimAndDrainAsync(id, drainer);

        var (statusAfter1, attemptsAfter1, next1) = await ReadRowAsync(id);
        Assert.Equal((int)OutboxStatus.Failed, statusAfter1); // 3
        Assert.Equal(1, attemptsAfter1);
        Assert.True(next1 > beforeFirst, "next_attempt_at should advance after a failure");

        // Drive the remaining attempts; each one is forced due so the claim picks it up again.
        var lastNext = next1;
        for (var attempt = 2; attempt <= MaxAttempts; attempt++)
        {
            await ForceClaimAndDrainAsync(id, drainer);
            var (_, attemptsNow, nextNow) = await ReadRowAsync(id);
            Assert.Equal(attempt, attemptsNow);
            if (attempt < MaxAttempts)
            {
                Assert.True(nextNow >= lastNext, "next_attempt_at should keep advancing across retries");
            }

            lastNext = nextNow;
        }

        var (finalStatus, finalAttempts, _) = await ReadRowAsync(id);
        Assert.Equal((int)OutboxStatus.Dead, finalStatus); // 4
        Assert.Equal(MaxAttempts, finalAttempts);
        Assert.Equal(MaxAttempts, recorder.Attempts);
    }

    [Fact]
    public async Task Sender_throwing_FormatException_dead_letters_immediately_without_retry()
    {
        var sender = new ThrowingEmailSender(new FormatException("malformed recipient address"));
        await using var provider = BuildProvider(sender);

        var id = await EnqueueEmailAsync(provider, "not-an-address", "Hi", "Body");

        var drainer = CreateDrainer(provider);
        await DrainAsync(drainer);

        Assert.Equal(1, sender.Attempts);

        var (status, attempts, _) = await ReadRowAsync(id);
        Assert.Equal((int)OutboxStatus.Dead, status); // 4 — permanent, no retry
        Assert.Equal(1, attempts);
    }

    private const int MaxAttempts = 3;

    private ServiceProvider BuildProvider(IEmailSender emailSender)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = ConnString })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(configuration);
        services.AddScoped<ITenantContext>(_ => new TenantContext(new TenantId("acme")));
        services.AddThemiaPostgres<TestNotificationsDbContext>(configuration);
        services.AddThemiaDataRepositories<TestNotificationsDbContext>();
        services.AddScoped<IOutboxStore, OutboxStore>();
        services.AddScoped(_ => emailSender);
        services.AddThemiaNotificationsPostgreSql();

        return services.BuildServiceProvider();
    }

    private OutboxDrainer CreateDrainer(ServiceProvider provider)
    {
        var dialect = provider.GetRequiredService<INotificationsSqlDialect>();
        var options = new NotificationsModuleOptions { MaxAttempts = MaxAttempts, MaxBatchSize = 10 };
        return new OutboxDrainer(
            dialect,
            new DrainSignal(),
            provider.GetRequiredService<IServiceScopeFactory>(),
            options,
            TimeProvider.System,
            NullLogger<OutboxDrainer>.Instance);
    }

    private static async Task<int> DrainAsync(OutboxDrainer drainer) =>
        await drainer.DrainOnceAsync(CancellationToken.None);

    // After a failure the row's next_attempt_at sits in the future; reset it to now so the next claim is due.
    private async Task ForceClaimAndDrainAsync(Guid id, OutboxDrainer drainer)
    {
        await SetDueNowAsync(id);
        await DrainAsync(drainer);
    }

    private async Task<Guid> EnqueueEmailAsync(ServiceProvider provider, string recipient, string subject, string body)
    {
        await using var scope = provider.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IOutboxStore>();
        var uow = scope.ServiceProvider.GetRequiredService<IUnitOfWork>();

        var now = DateTimeOffset.UtcNow;
        var message = new OutboxMessage
        {
            Channel = NotificationChannel.Email,
            Recipient = recipient,
            Subject = subject,
            Body = body,
            Status = OutboxStatus.Pending,
            Attempts = 0,
            NextAttemptAt = now,
            CreatedAt = now,
        };
        message.SetId(Guid.CreateVersion7());
        await store.EnqueueAsync(message, CancellationToken.None);
        await uow.SaveChangesAsync(CancellationToken.None);
        return message.Id;
    }

    private async Task SetDueNowAsync(Guid id)
    {
        await using var connection = new NpgsqlConnection(ConnString);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = new NpgsqlCommand(
            "UPDATE notifications.outbox_messages SET next_attempt_at = @now, lease_owner = NULL, lease_expires_at = NULL WHERE id = @id",
            connection);
        command.Parameters.AddWithValue("now", DateTimeOffset.UtcNow);
        command.Parameters.AddWithValue("id", id);
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }

    private async Task<(int Status, int Attempts, DateTimeOffset NextAttemptAt)> ReadRowAsync(Guid id)
    {
        await using var connection = new NpgsqlConnection(ConnString);
        await connection.OpenAsync(CancellationToken.None);
        await using var command = new NpgsqlCommand(
            "SELECT status, attempts, next_attempt_at FROM notifications.outbox_messages WHERE id = @id",
            connection);
        command.Parameters.AddWithValue("id", id);
        await using var reader = await command.ExecuteReaderAsync(CancellationToken.None);
        Assert.True(await reader.ReadAsync(CancellationToken.None), "row not found");
        return (reader.GetInt32(0), reader.GetInt32(1), reader.GetFieldValue<DateTimeOffset>(2));
    }

    private sealed class RecordingEmailSender(bool succeed) : IEmailSender
    {
        public List<NotificationMessage> Sent { get; } = [];

        public int Attempts { get; private set; }

        public Task<NotificationResult> SendAsync(NotificationMessage message, CancellationToken cancellationToken = default)
        {
            Attempts++;
            if (!succeed)
            {
                return Task.FromResult(NotificationResult.Failure("simulated provider rejection"));
            }

            Sent.Add(message);
            return Task.FromResult(NotificationResult.Success());
        }
    }

    private sealed class ThrowingEmailSender(Exception toThrow) : IEmailSender
    {
        public int Attempts { get; private set; }

        public Task<NotificationResult> SendAsync(NotificationMessage message, CancellationToken cancellationToken = default)
        {
            Attempts++;
            throw toThrow;
        }
    }
}
