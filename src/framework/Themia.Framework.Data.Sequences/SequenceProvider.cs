using System.Data;
using System.Data.Common;

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

    private readonly SequenceOptions options;
    private readonly ITenantContext tenantContext;
    private readonly ISequenceDialect dialect;

    public SequenceProvider(SequenceOptions options, ITenantContext tenantContext)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(tenantContext);
        options.Validate();

        this.options = options;
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
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        return AllocateAsync(RequireTenant(sequenceKey), sequenceKey, count, ct);
    }

    /// <inheritdoc />
    public Task EnsureSequenceAsync(string sequenceKey, long startValue = 1, CancellationToken ct = default) =>
        SeedAsync(RequireTenant(sequenceKey), sequenceKey, startValue, ct);

    /// <inheritdoc />
    public Task<long> NextHostAsync(string sequenceKey, CancellationToken ct = default)
    {
        RequireKey(sequenceKey);
        return AllocateAsync(HostTenant, sequenceKey, count: 1, ct).ContinueWithFirst();
    }

    /// <inheritdoc />
    public Task<IReadOnlyList<long>> NextHostRangeAsync(string sequenceKey, int count, CancellationToken ct = default)
    {
        RequireKey(sequenceKey);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);
        return AllocateAsync(HostTenant, sequenceKey, count, ct);
    }

    /// <inheritdoc />
    public Task EnsureHostSequenceAsync(string sequenceKey, long startValue = 1, CancellationToken ct = default)
    {
        RequireKey(sequenceKey);
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
        RequireKey(sequenceKey);

        return tenantContext.CurrentTenantId?.Value
            ?? throw new InvalidOperationException(
                $"Cannot allocate sequence '{sequenceKey}': there is no ambient tenant. Wrap the call in a "
                + "tenant scope (background jobs must use BackgroundTenantScope.Begin), or call the Host "
                + "overload if a host-level counter is what you meant.");
    }

    // ArgumentException.ThrowIfNullOrEmpty throws ArgumentNullException for a null argument — a subtype
    // that fails an exact-type Assert.ThrowsAsync<ArgumentException>. The interface promises plain
    // ArgumentException for "null or empty", so both cases are raised as the same, non-derived type here.
    private static void RequireKey(string sequenceKey)
    {
        if (string.IsNullOrEmpty(sequenceKey))
        {
            throw new ArgumentException("Sequence key must not be null or empty.", nameof(sequenceKey));
        }
    }

    private async Task<IReadOnlyList<long>> AllocateAsync(
        string tenant, string sequenceKey, int count, CancellationToken ct)
    {
        // Its own connection and transaction. This is the package: the number must survive the caller's
        // rollback, and it cannot if it shares the caller's transaction.
        await using var connection = dialect.CreateConnection(options.ConnectionString);
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

        await tx.CommitAsync(ct).ConfigureAwait(false);

        var allocated = new long[count];
        for (var i = 0; i < count; i++) allocated[i] = first + i;
        return allocated;
    }

    private async Task SeedAsync(string tenant, string sequenceKey, long startValue, CancellationToken ct)
    {
        await using var connection = dialect.CreateConnection(options.ConnectionString);
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
