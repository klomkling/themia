using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using Themia.Audit.Migrations;
using Themia.Data.Migrations;

namespace Themia.Audit.DependencyInjection;

/// <summary>
/// Order-free handshake between <see cref="AuditServiceCollectionExtensions.AddThemiaAudit"/> and the
/// three <c>AddThemiaAudit{Engine}</c> calls (design spec §5.2). Neither call runs the schema migration on
/// its own: each records its half — the migration intent (<c>runMigration</c> and the probed connection
/// string) or the <see cref="IMigrationEngineAdapter"/> — against the <see cref="IServiceCollection"/>
/// instance, and whichever call completes the pair runs the migration exactly once, honouring
/// <c>runMigration: false</c> in either call order. This is what lets the shipped README order
/// (<c>AddThemiaAudit</c> then <c>AddThemiaAudit{Engine}</c>) and its reverse both migrate, without ever
/// running unrequested DDL.
/// </summary>
/// <remarks>
/// State is per-<see cref="IServiceCollection"/>, tracked by reference identity in a
/// <see cref="ConditionalWeakTable{TKey,TValue}"/> so nothing needs to be added to the container itself.
/// Registration is consumed at registration time, which is single-threaded by convention (design §5.2), and
/// both halves are removed the instant a pair completes, so a redundant extra call to either method
/// afterwards is a no-op rather than a second migration attempt.
/// </remarks>
internal static class AuditMigrationHandshake
{
    private static readonly ConditionalWeakTable<IServiceCollection, Intent> Intents = new();
    private static readonly ConditionalWeakTable<IServiceCollection, IMigrationEngineAdapter> Adapters = new();

    /// <summary>
    /// Records the migration intent from <c>AddThemiaAudit</c> and completes the pair — running the
    /// migration, iff <paramref name="runMigration"/> is <see langword="true"/> — if the adapter half is
    /// already present.
    /// </summary>
    internal static void RecordIntent(IServiceCollection services, bool runMigration, string connectionString)
    {
        Intents.AddOrUpdate(services, new Intent(runMigration, connectionString));
        TryComplete(services);
    }

    /// <summary>
    /// Records the <see cref="IMigrationEngineAdapter"/> from an <c>AddThemiaAudit{Engine}</c> call and
    /// completes the pair if the intent half is already present.
    /// </summary>
    internal static void RecordAdapter(IServiceCollection services, IMigrationEngineAdapter adapter)
    {
        Adapters.AddOrUpdate(services, adapter);
        TryComplete(services);
    }

    private static void TryComplete(IServiceCollection services)
    {
        if (!Intents.TryGetValue(services, out var intent) || !Adapters.TryGetValue(services, out var adapter))
        {
            return;
        }

        // Consumed before running, not after: a migration failure must not leave the pair looking "still
        // pending" to a caller that inspects/retries registration.
        Intents.Remove(services);
        Adapters.Remove(services);

        if (intent.RunMigration)
        {
            ThemiaMigrations.Run(adapter, intent.ConnectionString, typeof(AuditSchemaMigration).Assembly);
        }
    }

    private sealed record Intent(bool RunMigration, string ConnectionString);
}
