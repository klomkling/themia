using Microsoft.Extensions.DependencyInjection;

using Themia.Audit.DependencyInjection;
using Themia.Audit.PostgreSql;

using Xunit;

namespace Themia.Audit.IntegrationTests;

/// <summary>
/// The <c>runMigration</c> handshake between <c>AddThemiaAudit</c> and <c>AddThemiaAuditPostgreSql</c> is
/// order-free (design §5.2): neither call migrates alone, and whichever call completes the pair runs the
/// migration exactly once, honouring <c>runMigration: false</c> regardless of call order. No container is
/// needed here — a malformed connection string makes a genuine migration attempt fail fast and
/// deterministically (the same technique <c>AuditServiceCollectionExtensionsTests</c> uses in
/// <c>Themia.Audit.Tests</c>), which is enough to prove an attempt happened, and exactly where it happened:
/// each test wraps only the call expected to trigger it in <see cref="Assert.Throws{T}(System.Func{object})"/>
/// — the other, unwrapped call must return normally, or the test errors out instead of merely failing an
/// assertion. The engine-first order and both <c>runMigration: false</c> cases have no coverage before this
/// file — the reverse order because the published README only shows the forward order, and the
/// <c>false</c> cases because they are the ones where a regression would run DDL nobody asked for.
/// </summary>
[Trait("Category", "Integration")]
public sealed class AuditMigrationHandshakeTests
{
    private const string MalformedConnectionString = "this is not a connection string";

    [Fact]
    public void Core_first_then_engine_with_runMigration_true_migrates_exactly_once()
    {
        var services = new ServiceCollection();

        // Recording the intent must not itself attempt anything — the adapter half is still missing.
        services.AddThemiaAudit(o =>
        {
            o.ConnectionString = MalformedConnectionString;
            o.Engine = AuditEngine.Postgres;
        });

        // Completing the pair is what triggers the (failing, by design) migration attempt.
        var ex = Assert.Throws<InvalidOperationException>(() => services.AddThemiaAuditPostgreSql());
        Assert.Contains("PostgreSQL", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Engine_first_then_core_with_runMigration_true_migrates_exactly_once()
    {
        var services = new ServiceCollection();

        // Recording the adapter must not itself attempt anything — the intent half is still missing.
        services.AddThemiaAuditPostgreSql();

        // Completing the pair is what triggers the (failing, by design) migration attempt.
        var ex = Assert.Throws<InvalidOperationException>(() => services.AddThemiaAudit(o =>
        {
            o.ConnectionString = MalformedConnectionString;
            o.Engine = AuditEngine.Postgres;
        }));
        Assert.Contains("PostgreSQL", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_second_call_with_runMigration_false_does_not_cancel_an_earlier_true()
    {
        var services = new ServiceCollection();

        services.AddThemiaAudit(o =>
        {
            o.ConnectionString = MalformedConnectionString;
            o.Engine = AuditEngine.Postgres;
        });

        // The intent is recorded per IServiceCollection, so a later call could overwrite it. It must not:
        // last-wins would let this second call's runMigration cancel a migration the first call asked for,
        // and the schema would silently never be created. An extra migration is idempotent; a skipped one
        // is a missing table at the first write.
        services.AddThemiaAudit(o =>
        {
            o.ConnectionString = MalformedConnectionString;
            o.Engine = AuditEngine.Postgres;
        }, runMigration: false);

        // Completing the pair must still trigger the (failing, by design) migration attempt.
        var ex = Assert.Throws<InvalidOperationException>(() => services.AddThemiaAuditPostgreSql());
        Assert.Contains("PostgreSQL", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_second_call_with_runMigration_true_after_a_false_one_migrates()
    {
        var services = new ServiceCollection();

        services.AddThemiaAudit(o =>
        {
            o.ConnectionString = MalformedConnectionString;
            o.Engine = AuditEngine.Postgres;
        }, runMigration: false);

        // The opposite order of the test above. Recording true after false must take effect, so that the
        // rule is "a requested migration wins", not merely "the first call wins".
        services.AddThemiaAudit(o =>
        {
            o.ConnectionString = MalformedConnectionString;
            o.Engine = AuditEngine.Postgres;
        });

        var ex = Assert.Throws<InvalidOperationException>(() => services.AddThemiaAuditPostgreSql());
        Assert.Contains("PostgreSQL", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_redundant_call_after_the_pair_completed_does_not_migrate_again()
    {
        var services = new ServiceCollection();

        services.AddThemiaAudit(o =>
        {
            o.ConnectionString = MalformedConnectionString;
            o.Engine = AuditEngine.Postgres;
        });
        Assert.Throws<InvalidOperationException>(() => services.AddThemiaAuditPostgreSql());

        // The pair is consumed, so neither half may start a second attempt — otherwise the "never
        // downgrade a recorded true" rule above would turn every redundant registration into fresh DDL.
        Assert.Null(Record.Exception(() => services.AddThemiaAudit(o =>
        {
            o.ConnectionString = MalformedConnectionString;
            o.Engine = AuditEngine.Postgres;
        })));
        Assert.Null(Record.Exception(() => services.AddThemiaAuditPostgreSql()));
    }

    [Fact]
    public void Core_first_then_engine_with_runMigration_false_produces_no_ddl()
    {
        var services = new ServiceCollection();

        services.AddThemiaAudit(o =>
        {
            o.ConnectionString = MalformedConnectionString;
            o.Engine = AuditEngine.Postgres;
        }, runMigration: false);

        // Completing the pair must still not attempt anything: runMigration: false means no DDL in
        // either order, so this must not throw even though the connection string is unusable.
        var ex = Record.Exception(() => services.AddThemiaAuditPostgreSql());
        Assert.Null(ex);
    }

    [Fact]
    public void Engine_first_then_core_with_runMigration_false_produces_no_ddl()
    {
        var services = new ServiceCollection();

        services.AddThemiaAuditPostgreSql();

        // Completing the pair must still not attempt anything: runMigration: false means no DDL in
        // either order, so this must not throw even though the connection string is unusable.
        var ex = Record.Exception(() => services.AddThemiaAudit(o =>
        {
            o.ConnectionString = MalformedConnectionString;
            o.Engine = AuditEngine.Postgres;
        }, runMigration: false));
        Assert.Null(ex);
    }
}
