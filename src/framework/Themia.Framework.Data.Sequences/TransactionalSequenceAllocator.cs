using System.Data.Common;

using Dapper;

using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Sequences.Dialects;

namespace Themia.Framework.Data.Sequences;

/// <summary>
/// Allocates sequence values inside the caller's open <see cref="DbTransaction"/>.
/// </summary>
internal sealed class TransactionalSequenceAllocator : ITransactionalSequenceAllocator
{
    private const string HostTenant = "";

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

        // Lazy seeding inside the caller's transaction: ensures row exists race-free without requiring a separate pre-seed step.
        await connection.ExecuteAsync(new CommandDefinition(
            dialect.InsertIfMissingSql, new { tenant, key = sequenceKey, val = 1L }, tx, cancellationToken: ct))
            .ConfigureAwait(false);

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
