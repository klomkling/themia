using System.Data.Common;

using Dapper;

using MySqlConnector;

using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Sequences.Dialects;

namespace Themia.Framework.Data.Sequences;

/// <summary>
/// Allocates sequence values inside the caller's open <see cref="DbTransaction"/>.
/// </summary>
internal sealed class TransactionalSequenceAllocator : ITransactionalSequenceAllocator
{
    private const string HostTenant = "";
    private const int SideSeedAttempts = 3;

    private readonly ITenantContext tenantContext;
    private readonly ISequenceDialect dialect;

    public TransactionalSequenceAllocator(SequenceOptions options, ITenantContext tenantContext)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(tenantContext);
        options.Validate();

        this.tenantContext = tenantContext;
        dialect = options.Dialect ?? SequenceDialectFactory.For(options.Engine);
    }

    /// <inheritdoc />
    public Task<long> NextInTransactionAsync(DbTransaction tx, string sequenceKey, CancellationToken ct = default)
    {
        var tenant = RequireTenant(sequenceKey);
        return AllocateInTransactionAsync(tx, tenant, sequenceKey, ct);
    }

    /// <inheritdoc />
    public Task<long> NextHostInTransactionAsync(DbTransaction tx, string sequenceKey, CancellationToken ct = default)
    {
        ValidateSequenceKey(sequenceKey);
        return AllocateInTransactionAsync(tx, HostTenant, sequenceKey, ct);
    }

    private async Task<long> AllocateInTransactionAsync(
        DbTransaction tx, string tenant, string sequenceKey, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(tx);

        var connection = tx.Connection
            ?? throw new InvalidOperationException(
                $"Cannot allocate sequence '{sequenceKey}': the provided transaction has no associated connection.");

        await SeedIfMissingAsync(tx, connection, tenant, sequenceKey, ct).ConfigureAwait(false);

        var current = await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
            dialect.SelectForUpdateSql, new { tenant, key = sequenceKey }, tx, cancellationToken: ct))
            .ConfigureAwait(false);

        if (current is null)
        {
            throw new InvalidOperationException(
                $"Sequence '{sequenceKey}' could not be locked or found for "
                + $"{(tenant.Length == 0 ? "the host" : $"tenant '{tenant}'")}.");
        }

        var valueToReturn = current.Value;

        long advanced;
        try
        {
            advanced = checked(valueToReturn + 1);
        }
        catch (OverflowException ex)
        {
            throw new InvalidOperationException(
                $"Sequence '{sequenceKey}' is exhausted: next_value ({valueToReturn}) cannot advance by 1 "
                + "without exceeding long.MaxValue.", ex);
        }

        await connection.ExecuteAsync(new CommandDefinition(
            dialect.UpdateNextValueSql, new { tenant, key = sequenceKey, val = advanced }, tx, cancellationToken: ct))
            .ConfigureAwait(false);

        return valueToReturn;
    }

    /// <summary>
    /// Lazy seeding: makes sure the row exists without requiring a separate pre-seed step.
    /// </summary>
    /// <remarks>
    /// On MySQL the seed commits independently of the caller's transaction. An uncommitted seed insert
    /// that later rolls back makes every queued seeder of the same new key wake at once, and InnoDB
    /// deadlocks them (error 1213); the victim loses its whole caller transaction, so the statement cannot be
    /// retried in place. The seed row is always <c>next_value = 1</c>, so a caller rollback leaves exactly the
    /// value an unseeded key would start at: gap-freeness still comes only from the in-transaction
    /// <c>FOR UPDATE</c> and <c>UPDATE</c>. A connection that is not a <see cref="MySqlConnection"/> keeps the
    /// in-transaction seed.
    /// </remarks>
    private async Task SeedIfMissingAsync(
        DbTransaction tx, DbConnection connection, string tenant, string sequenceKey, CancellationToken ct)
    {
        if (dialect is not MySqlSequenceDialect mySql || connection is not MySqlConnection mySqlConnection)
        {
            await connection.ExecuteAsync(new CommandDefinition(
                dialect.InsertIfMissingSql, new { tenant, key = sequenceKey, val = 1L }, tx, cancellationToken: ct))
                .ConfigureAwait(false);
            return;
        }

        // Only the first use of a key pays for a side connection. A second allocation of the same key inside one
        // caller transaction must not reach the side seed: ON DUPLICATE KEY UPDATE takes an X lock on the row the
        // caller already holds, and the caller would wait on its own lock until innodb_lock_wait_timeout.
        var exists = await connection.QuerySingleOrDefaultAsync<int?>(new CommandDefinition(
            mySql.ExistsSql, new { tenant, key = sequenceKey }, tx, cancellationToken: ct))
            .ConfigureAwait(false);
        if (exists is not null)
        {
            return;
        }

        await SeedOnSideConnectionAsync(mySql, mySqlConnection, tenant, sequenceKey, ct).ConfigureAwait(false);
    }

    private static async Task SeedOnSideConnectionAsync(
        MySqlSequenceDialect mySql, MySqlConnection callerConnection, string tenant, string sequenceKey,
        CancellationToken ct)
    {
        // The caller built its connection, so AutoEnlist is whatever they left it (default true). Set it off
        // explicitly: joining an ambient System.Transactions scope would let their rollback take the seed back.
        // A different connection string also means a separate pool, so the caller's pool cannot be starved.
        var sideString = new MySqlConnectionStringBuilder(callerConnection.ConnectionString) { AutoEnlist = false }
            .ConnectionString;

        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await using var side = callerConnection.CloneWith(sideString);
                await side.OpenAsync(ct).ConfigureAwait(false);
                await side.ExecuteAsync(new CommandDefinition(
                    mySql.InsertIfMissingSql, new { tenant, key = sequenceKey, val = 1L }, cancellationToken: ct))
                    .ConfigureAwait(false);
                return;
            }
            catch (MySqlException ex) when (attempt < SideSeedAttempts
                && ex.ErrorCode is MySqlErrorCode.LockDeadlock or MySqlErrorCode.LockWaitTimeout)
            {
                // Autocommit on a side connection: a deadlock victim loses only the seed, so retrying is safe.
            }
        }
    }

    private string RequireTenant(string sequenceKey)
    {
        ValidateSequenceKey(sequenceKey);

        return tenantContext.CurrentTenantId?.Value
            ?? throw new InvalidOperationException(
                $"Cannot allocate sequence '{sequenceKey}': there is no ambient tenant. Wrap the call in a "
                + "tenant scope (background jobs must use BackgroundTenantScope.Begin), or call the Host "
                + "overload if a host-level counter is what you meant.");
    }

    private static void ValidateSequenceKey(string sequenceKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(sequenceKey);

        if (sequenceKey.Length > SequenceProvider.MaxSequenceKeyLength)
        {
            throw new ArgumentException(
                $"sequenceKey is {sequenceKey.Length} characters long, exceeding the "
                + $"{SequenceProvider.MaxSequenceKeyLength}-character column limit.",
                nameof(sequenceKey));
        }
    }
}
