using System.Data;

using Dapper;

using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Sequences.Dialects;

namespace Themia.Framework.Data.Sequences;

/// <summary>
/// Allocates sequence values on its OWN connection and transaction, so the value survives the calling
/// transaction's rollback.
/// </summary>
internal sealed class SequenceProvider : ISequenceProvider
{
    /// <summary>Host-level rows use the empty string. <c>TenantId</c>'s constructor rejects null and
    /// whitespace, so no real tenant can ever collide with it.</summary>
    private const string HostTenant = "";

    /// <summary>
    /// The <c>sequence_key</c> column width. Must agree with <c>SequencesSchemaMigration</c>'s
    /// <c>.AsString(SequenceProvider.MaxSequenceKeyLength)</c> — MySQL silently truncates an over-length
    /// key to fit the column instead of erroring, so this guard is what actually rejects it, on every
    /// engine, before it ever reaches the database.
    /// </summary>
    internal const int MaxSequenceKeyLength = 100;

    private readonly string connectionString;
    private readonly ITenantContext tenantContext;
    private readonly ISequenceDialect dialect;

    public SequenceProvider(SequenceOptions options, ITenantContext tenantContext)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(tenantContext);
        options.Validate();

        // Snapshotted together so the two can never drift apart: reading options.ConnectionString live on
        // every call, while the dialect was snapshotted here, let a caller who mutates a shared
        // SequenceOptions after construction run the OLD engine's dialect SQL against a NEW connection
        // string.
        connectionString = options.ConnectionString;
        this.tenantContext = tenantContext;
        dialect = options.Dialect ?? SequenceDialectFactory.For(options.Engine);
    }

    /// <inheritdoc />
    public Task<long> NextAsync(string sequenceKey, CancellationToken ct = default) =>
        AllocateAsync(RequireTenant(sequenceKey), sequenceKey, count: 1, ct)
            .ContinueWithFirst();

    /// <inheritdoc />
    public Task<IReadOnlyList<long>> NextRangeAsync(string sequenceKey, int count, CancellationToken ct = default)
    {
        var tenant = RequireTenant(sequenceKey);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        return AllocateAsync(tenant, sequenceKey, count, ct);
    }

    /// <inheritdoc />
    public Task EnsureSequenceAsync(string sequenceKey, long startValue = 1, CancellationToken ct = default) =>
        SeedAsync(RequireTenant(sequenceKey), sequenceKey, startValue, ct);

    /// <inheritdoc />
    public Task<long> NextHostAsync(string sequenceKey, CancellationToken ct = default)
    {
        ValidateSequenceKey(sequenceKey);
        return AllocateAsync(HostTenant, sequenceKey, count: 1, ct).ContinueWithFirst();
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<long>> NextHostRangeAsync(string sequenceKey, int count, CancellationToken ct = default)
    {
        ValidateSequenceKey(sequenceKey);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        return AllocateAsync(HostTenant, sequenceKey, count, ct);
    }

    /// <inheritdoc />
    public Task EnsureHostSequenceAsync(string sequenceKey, long startValue = 1, CancellationToken ct = default)
    {
        ValidateSequenceKey(sequenceKey);
        return SeedAsync(HostTenant, sequenceKey, startValue, ct);
    }

    /// <summary>
    /// Resolves the ambient tenant, refusing to fall back to the host row.
    /// </summary>
    /// <remarks>
    /// Background work only has an ambient tenant if it opted in (<c>BackgroundTenantScope.Begin</c>).
    /// Treating "no tenant" as host-level would let a job that lost its scope draw every tenant's invoice
    /// numbers from one shared counter, with nothing reporting it. Host allocation must be asked for.
    /// </remarks>
    private string RequireTenant(string sequenceKey)
    {
        ValidateSequenceKey(sequenceKey);

        return tenantContext.CurrentTenantId?.Value
            ?? throw new InvalidOperationException(
                $"Cannot allocate sequence '{sequenceKey}': there is no ambient tenant. Wrap the call in a "
                + "tenant scope (background jobs must use BackgroundTenantScope.Begin), or call the Host "
                + "overload if a host-level counter is what you meant.");
    }

    /// <summary>
    /// Rejects a null/empty key, or one exceeding <see cref="MaxSequenceKeyLength"/>, on every engine —
    /// rather than letting MySQL alone silently truncate it into a different (and possibly colliding)
    /// bucket.
    /// </summary>
    private static void ValidateSequenceKey(string sequenceKey)
    {
        ArgumentException.ThrowIfNullOrEmpty(sequenceKey);

        if (sequenceKey.Length > MaxSequenceKeyLength)
        {
            throw new ArgumentException(
                $"sequenceKey is {sequenceKey.Length} characters long, exceeding the "
                + $"{MaxSequenceKeyLength}-character column limit.",
                nameof(sequenceKey));
        }
    }

    private async Task<IReadOnlyList<long>> AllocateAsync(
        string tenant, string sequenceKey, int count, CancellationToken ct)
    {
        // Its own connection and transaction. This is the package: the number must survive the caller's
        // rollback, and it cannot if it shares the caller's transaction.
        await using var connection = dialect.CreateConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, ct)
            .ConfigureAwait(false);

        var current = await connection.QuerySingleOrDefaultAsync<long?>(new CommandDefinition(
            dialect.SelectForUpdateSql, new { tenant, key = sequenceKey }, tx, cancellationToken: ct))
            .ConfigureAwait(false);

        if (current is null) throw NotSeeded(tenant, sequenceKey);

        var first = current.Value;

        // Overflow is a loud failure. Unchecked, `+ count` wraps to negative at long.MaxValue and the
        // wrapped values collide with real ones once the counter comes back round.
        long advanced;
        try
        {
            advanced = checked(first + count);
        }
        catch (OverflowException ex)
        {
            throw new InvalidOperationException(
                $"Sequence '{sequenceKey}' is exhausted: next_value ({first}) cannot advance by {count} "
                + "without exceeding long.MaxValue.", ex);
        }

        await connection.ExecuteAsync(new CommandDefinition(
            dialect.UpdateNextValueSql, new { tenant, key = sequenceKey, val = advanced }, tx,
            cancellationToken: ct)).ConfigureAwait(false);

        // Allocated BEFORE the commit: NextRangeAsync(key, int.MaxValue) allocating this array after the
        // commit would mean the advance is already durable when an OutOfMemoryException hits, so the
        // range is spent and the caller never receives it. Allocating first lets that failure happen while
        // the transaction can still be rolled back.
        var allocated = new long[count];
        for (var i = 0; i < count; i++) allocated[i] = first + i;

        await tx.CommitAsync(ct).ConfigureAwait(false);

        return allocated;
    }

    private async Task SeedAsync(string tenant, string sequenceKey, long startValue, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(startValue);

        await using var connection = dialect.CreateConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        await connection.ExecuteAsync(new CommandDefinition(
            dialect.InsertIfMissingSql, new { tenant, key = sequenceKey, val = startValue },
            cancellationToken: ct)).ConfigureAwait(false);
    }

    private static InvalidOperationException NotSeeded(string tenant, string sequenceKey) =>
        new($"Sequence '{sequenceKey}' has not been seeded for "
            + $"{(tenant.Length == 0 ? "the host" : $"tenant '{tenant}'")}. "
            + "Call EnsureSequenceAsync (or EnsureHostSequenceAsync) first.");
}

/// <summary>Reduces a single-value allocation to its one element.</summary>
internal static class SequenceTaskExtensions
{
    public static async Task<long> ContinueWithFirst(this Task<IReadOnlyList<long>> task) =>
        (await task.ConfigureAwait(false))[0];
}
