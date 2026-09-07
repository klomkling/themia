using Microsoft.Extensions.DependencyInjection;

using Themia.Audit.DependencyInjection;
using Themia.Audit.PostgreSql;

using Xunit;

namespace Themia.Audit.IntegrationTests;

/// <summary>
/// Proves the neutral-only consumer path (design §11): <c>AddThemiaAudit</c> plus a dialect package and
/// no <c>Themia.Modules.Audit</c> module still yields a working recorder. Joins the shared Postgres
/// container (see <see cref="AuditStoreFixture"/>'s remarks) — the table already exists from the store
/// and migration tests that share this collection, so this asserts a recorded row is readable back, not
/// that the table was freshly created or is empty.
/// </summary>
[Trait("Category", "Integration")]
[Collection(PostgresAuditStoreCollection.Name)]
public sealed class NeutralOnlyMigrationTests(PostgresAuditStoreFixture fixture)
{
    [Fact]
    public async Task AddThemiaAudit_with_no_module_registered_yields_a_working_recorder()
    {
        var services = new ServiceCollection();
        services.AddThemiaAudit(o =>
        {
            o.ConnectionString = fixture.ConnectionString;
            o.Engine = AuditEngine.Postgres;
        });
        services.AddThemiaAuditPostgreSql();

        await using var provider = services.BuildServiceProvider();
        var recorder = provider.GetRequiredService<IAuditRecorder>();

        // Namespaced with a per-test correlation id so this row is unmistakably this test's, even
        // though every class in this collection shares one table on one container (see
        // AuditStoreFixture's remarks) — never assert a total row count in this project.
        var correlationId = $"c-{Guid.NewGuid():N}";
        var entry = new AuditEntry
        {
            EventType = "NEUTRAL_ONLY_TEST",
            Category = AuditCategory.Activity,
            Outcome = AuditOutcome.Success,
            CorrelationId = correlationId,
        };

        var eventUid = await recorder.RecordAsync(entry);

        var store = provider.GetRequiredService<IAuditStore>();
        var dialect = provider.GetRequiredService<IAuditDialect>();
        await using var connection = dialect.CreateConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        var read = await store.GetAsync(eventUid, connection, default);

        Assert.NotNull(read);
        Assert.Equal(correlationId, read!.CorrelationId);
        Assert.Equal("NEUTRAL_ONLY_TEST", read.EventType);
    }
}
