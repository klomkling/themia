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
/// Registration is consumed at registration time, which is single-threaded by convention (design §5.2).
/// <para>
/// Two rules keep the half-recorded state honest:
/// </para>
/// <list type="bullet">
/// <item><description>
/// <b>A requested migration is never silently downgraded.</b> <c>runMigration</c> accumulates with OR, so a
/// later <c>AddThemiaAudit(cfg, runMigration: false)</c> cannot cancel an earlier <c>true</c>. Last-wins
/// would have made the *second* call's default decide whether the schema exists, and the failure mode of
/// picking wrongly is asymmetric: an extra migration is idempotent (FluentMigrator skips what
/// <c>VersionInfo</c> already records), a skipped one is a missing table discovered at the first write.
/// </description></item>
/// <item><description>
/// <b>A completed pair stays completed.</b> Once the migration has run (or been declined), the handshake is
/// marked <see cref="Handshake.Completed"/> and further calls to either half are no-ops, so a redundant
/// extra registration cannot trigger a second migration attempt.
/// </description></item>
/// </list>
/// <para>
/// An intent that asked for a migration and never met an adapter is a real defect — the adopter gets no
/// table and no error — so <see cref="IsMigrationPending"/> exposes it for
/// <c>AddThemiaAudit</c>'s startup validation to fail on.
/// </para>
/// </remarks>
internal static class AuditMigrationHandshake
{
    private static readonly ConditionalWeakTable<IServiceCollection, Handshake> States = new();

    /// <summary>
    /// Records the migration intent from <c>AddThemiaAudit</c> and completes the pair — running the
    /// migration, iff <paramref name="runMigration"/> is <see langword="true"/> — if the adapter half is
    /// already present.
    /// </summary>
    internal static void RecordIntent(IServiceCollection services, bool runMigration, string connectionString)
    {
        var state = States.GetOrCreateValue(services);
        if (state.Completed)
        {
            return;
        }

        state.IntentRecorded = true;

        // OR, not assignment: see the "never silently downgraded" rule in the class remarks.
        state.RunMigration |= runMigration;

        // Only the runMigration: true branch probes a connection string; the false branch passes
        // string.Empty, which must not overwrite one an earlier true already recorded.
        if (runMigration)
        {
            state.ConnectionString = connectionString;
        }

        TryComplete(services, state);
    }

    /// <summary>
    /// Records the <see cref="IMigrationEngineAdapter"/> from an <c>AddThemiaAudit{Engine}</c> call and
    /// completes the pair if the intent half is already present.
    /// </summary>
    internal static void RecordAdapter(IServiceCollection services, IMigrationEngineAdapter adapter)
    {
        var state = States.GetOrCreateValue(services);
        if (state.Completed)
        {
            return;
        }

        state.Adapter = adapter;
        TryComplete(services, state);
    }

    /// <summary>
    /// Whether <paramref name="services"/> carries an intent that asked for a migration and has not been
    /// paired with an engine adapter. True means the schema will not be created: the adopter is using the
    /// neutral core with their own <see cref="IAuditDialect"/>/<see cref="IAuditStore"/> and never called an
    /// <c>AddThemiaAudit{Engine}</c>. Read at startup by <c>AddThemiaAudit</c>'s options validation.
    /// </summary>
    internal static bool IsMigrationPending(IServiceCollection services) =>
        States.TryGetValue(services, out var state) && state is { Completed: false, IntentRecorded: true, RunMigration: true };

    private static void TryComplete(IServiceCollection services, Handshake state)
    {
        if (!state.IntentRecorded || state.Adapter is null)
        {
            return;
        }

        // Marked complete before running, not after: a migration failure must not leave the pair looking
        // "still pending" to a caller that inspects/retries registration — nor make the startup validation
        // report a missing engine package when the engine was there and the migration itself failed.
        state.Completed = true;

        if (state.RunMigration)
        {
            ThemiaMigrations.Run(state.Adapter, state.ConnectionString, typeof(AuditSchemaMigration).Assembly);
        }
    }

    private sealed class Handshake
    {
        public bool IntentRecorded { get; set; }

        public bool RunMigration { get; set; }

        public string ConnectionString { get; set; } = string.Empty;

        public IMigrationEngineAdapter? Adapter { get; set; }

        public bool Completed { get; set; }
    }
}
