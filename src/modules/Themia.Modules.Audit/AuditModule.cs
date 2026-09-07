using System.Data.Common;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Themia.Audit;
using Themia.Framework.Core.Modules;

namespace Themia.Modules.Audit;

/// <summary>
/// The audit module: asserts that <c>themia_audit_events</c> already exists. Does not run the schema
/// migration itself (design §11) — <c>AddThemiaAudit</c> in the neutral <c>Themia.Audit</c> core owns
/// that, because a consumer using <c>Themia.Audit</c> with no module layer at all must still get a table.
/// The host wires the services via <c>AddThemiaAuditModule(...)</c>; this module exists for hosts that
/// drive modules through the <see cref="IThemiaModule"/> convention.
/// </summary>
public sealed class AuditModule : ThemiaModuleBase
{
    /// <inheritdoc />
    public override ModuleDescriptor Descriptor { get; } = new(
        name: "Themia.Audit",
        displayName: "Audit",
        description: "Transaction-enlisted audit recording, tenant-scoped reads, and the IAuditLogService adapter.",
        version: new Version(0, 23, 0, 0));

    /// <inheritdoc />
    /// <exception cref="InvalidOperationException">
    /// The <c>themia_audit_events</c> table does not exist. Naming the missing table in the message is
    /// deliberate — the alternative is a raw driver error the first time an audit write is attempted.
    /// </exception>
    public override async ValueTask InitializeAsync(IServiceProvider serviceProvider, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);
        cancellationToken.ThrowIfCancellationRequested();

        using var scope = serviceProvider.CreateScope();
        var sp = scope.ServiceProvider;

        var options = sp.GetRequiredService<IOptions<AuditOptions>>().Value;
        var dialect = sp.GetRequiredService<IAuditDialect>();
        var store = sp.GetRequiredService<IAuditStore>();

        await AssertSchemaExistsAsync(store, dialect, options.ConnectionString, cancellationToken).ConfigureAwait(false);
    }

    // GetAsync, not QueryAsync. QueryAsync issues TWO statements — the page select AND the dialect's
    // CountSql, which carries no predicate at all. On an append-only table whose retention defaults to
    // keep-forever, that is a full scan on every host start and every pod restart, so the probe meant to
    // be the cheapest thing in the boot path was the most expensive. GetAsync(Guid.Empty) is one indexed
    // lookup against ux_themia_audit_events_event_uid that matches nothing and reads no rows, and still
    // fails exactly the way a real audit write would if the table were missing — no raw SQL text of our
    // own, and no dependency on FluentMigrator.
    private static async Task AssertSchemaExistsAsync(
        IAuditStore store, IAuditDialect dialect, string connectionString, CancellationToken cancellationToken)
    {
        await using var connection = dialect.CreateConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            await store.GetAsync(Guid.Empty, connection, cancellationToken).ConfigureAwait(false);
        }
        catch (DbException ex)
        {
            // Missing table is the common case, but this catch also fires on a permission error, a
            // timeout, or bad credentials — for which "the table must exist" sends an operator hunting
            // for a table that is already there. Keep the original exception as InnerException and fold
            // its message in, so the real cause survives instead of being asserted over.
            throw new InvalidOperationException(
                "Themia.Modules.Audit could not read 'themia_audit_events'. If the table does not exist yet, " +
                "call AddThemiaAudit(...) with runMigration: true (the default), or run the Themia.Audit " +
                $"schema migration yourself, before starting this module. Underlying error: {ex.Message}", ex);
        }
    }
}
