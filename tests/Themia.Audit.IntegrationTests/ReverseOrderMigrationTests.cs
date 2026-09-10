using Microsoft.Extensions.DependencyInjection;

using Npgsql;

using Themia.Audit.DependencyInjection;
using Themia.Audit.PostgreSql;

using Xunit;

namespace Themia.Audit.IntegrationTests;

/// <summary>
/// The engine-first call order, against a database that starts empty.
/// </summary>
/// <remarks>
/// <see cref="NeutralOnlyMigrationTests"/> covers the order the README documents — <c>AddThemiaAudit</c>
/// then <c>AddThemiaAuditPostgreSql</c>. This covers the reverse, which is the order the handshake exists
/// for and the one that had no coverage before it.
/// <para>
/// The handshake's unit tests use a malformed connection string, so they prove <em>when</em> the attempt
/// happens and not what it produced: a handshake firing in this direction with the wrong connection string
/// or the wrong adapter would throw too, and they would still pass.
/// </para>
/// <para>
/// This test therefore creates its OWN database on the shared container rather than joining the store
/// collection. Every class in that collection inherits a database the fixture has already migrated
/// (<c>AuditStoreFixtures.cs</c> runs <c>ThemiaMigrations.Run</c> at startup), so a test written against it
/// passes whether or not the migration ran — which is exactly what a falsification run showed when this
/// test was first written that way.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
[Collection(PostgresAuditStoreCollection.Name)]
public sealed class ReverseOrderMigrationTests(PostgresAuditStoreFixture fixture)
{
    [Fact]
    public async Task Registering_the_engine_package_first_migrates_a_fresh_database()
    {
        var database = $"audit_reverse_{Guid.NewGuid():N}";
        var admin = new NpgsqlConnectionStringBuilder(fixture.ConnectionString);
        await using (var connection = new NpgsqlConnection(admin.ConnectionString))
        {
            await connection.OpenAsync();
            await using var create = connection.CreateCommand();
            create.CommandText = $"CREATE DATABASE \"{database}\"";
            await create.ExecuteNonQueryAsync();
        }

        var target = new NpgsqlConnectionStringBuilder(fixture.ConnectionString) { Database = database }
            .ConnectionString;

        var services = new ServiceCollection();

        // Reversed on purpose: the engine package goes first, so the adapter half of the handshake is
        // recorded before there is any intent for it to complete.
        services.AddThemiaAuditPostgreSql();
        services.AddThemiaAudit(o =>
        {
            o.ConnectionString = target;
            o.Engine = AuditEngine.Postgres;
        });

        await using var provider = services.BuildServiceProvider();
        var recorder = provider.GetRequiredService<IAuditRecorder>();

        var correlationId = $"c-{Guid.NewGuid():N}";
        var eventUid = await recorder.RecordAsync(new AuditEntry
        {
            EventType = "REVERSE_ORDER_TEST",
            Category = AuditCategory.Activity,
            Outcome = AuditOutcome.Success,
            CorrelationId = correlationId,
        });

        // The row only exists if the migration ran on THIS database, which nothing else has touched.
        var store = provider.GetRequiredService<IAuditStore>();
        var dialect = provider.GetRequiredService<IAuditDialect>();
        await using var read = dialect.CreateConnection(target);
        await read.OpenAsync();
        var entry = await store.GetAsync(eventUid, read, default);

        Assert.NotNull(entry);
        Assert.Equal(correlationId, entry!.CorrelationId);
    }
}
