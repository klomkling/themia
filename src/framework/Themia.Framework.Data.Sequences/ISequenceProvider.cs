namespace Themia.Framework.Data.Sequences;

/// <summary>
/// Atomic numeric sequence allocator. Each call returns a value no other concurrent caller can receive
/// for the same tenant and key.
/// </summary>
/// <remarks>
/// <para>
/// Allocation runs in its OWN transaction and survives the calling transaction's rollback. That is the
/// intended semantic: gaps in the allocated range are normal — a rolled-back caller produces one —
/// while duplicates are catastrophic. Invoice, order and document numbering is the canonical use.
/// </para>
/// <para>
/// It does NOT guarantee gapless numbering, and cannot: the value is allocated before the caller's own
/// transaction commits. A regulator requiring an unbroken run of numbers needs a different mechanism.
/// </para>
/// <para>
/// Values are <see cref="long"/>. Formatting (<c>INV-2026-00042</c>) is the caller's; the provider has
/// no opinion about prefixes, padding or when a counter resets.
/// </para>
/// </remarks>
public interface ISequenceProvider
{
    /// <summary>Allocates the next value for the CURRENT tenant.</summary>
    /// <param name="sequenceKey">Caller-defined key, conventionally colon-namespaced (<c>DocNo:Invoice:2026</c>).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The allocated value.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="sequenceKey"/> is null, empty, or exceeds the 100-character column limit.
    /// </exception>
    /// <exception cref="InvalidOperationException">
    /// There is no ambient tenant, or the sequence has not been seeded, or it is exhausted.
    /// </exception>
    Task<long> NextAsync(string sequenceKey, CancellationToken ct = default);

    /// <summary>Allocates <paramref name="count"/> contiguous values for the CURRENT tenant, ascending.</summary>
    /// <param name="sequenceKey">The sequence key.</param>
    /// <param name="count">How many values to allocate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The allocated values in ascending order.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="sequenceKey"/> is null, empty, or exceeds the 100-character column limit.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is not positive.</exception>
    /// <exception cref="InvalidOperationException">No ambient tenant, not seeded, or exhausted.</exception>
    Task<IReadOnlyList<long>> NextRangeAsync(string sequenceKey, int count, CancellationToken ct = default);

    /// <summary>Idempotently seeds the sequence for the CURRENT tenant.</summary>
    /// <param name="sequenceKey">The sequence key.</param>
    /// <param name="startValue">First value <see cref="NextAsync"/> returns. Ignored if the row exists.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="sequenceKey"/> is null, empty, or exceeds the 100-character column limit.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="startValue"/> is not positive.</exception>
    /// <exception cref="InvalidOperationException">There is no ambient tenant.</exception>
    Task EnsureSequenceAsync(string sequenceKey, long startValue = 1, CancellationToken ct = default);

    /// <summary>Allocates the next HOST-LEVEL value, outside any tenant.</summary>
    /// <param name="sequenceKey">The sequence key.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The allocated value.</returns>
    /// <remarks>
    /// A separate method rather than a null-tenant fallback on <see cref="NextAsync"/>. Background work
    /// only has an ambient tenant if it opted in (<c>BackgroundTenantScope.Begin</c>), so a job that lost
    /// its scope would otherwise draw every tenant's numbers from one shared counter with nothing
    /// reporting it. Host-level allocation has to be asked for.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// <paramref name="sequenceKey"/> is null, empty, or exceeds the 100-character column limit.
    /// </exception>
    /// <exception cref="InvalidOperationException">The sequence has not been seeded, or is exhausted.</exception>
    Task<long> NextHostAsync(string sequenceKey, CancellationToken ct = default);

    /// <summary>Allocates <paramref name="count"/> contiguous HOST-LEVEL values, ascending.</summary>
    /// <param name="sequenceKey">The sequence key.</param>
    /// <param name="count">How many values to allocate.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <returns>The allocated values in ascending order.</returns>
    /// <exception cref="ArgumentException">
    /// <paramref name="sequenceKey"/> is null, empty, or exceeds the 100-character column limit.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="count"/> is not positive.</exception>
    /// <exception cref="InvalidOperationException">Not seeded, or exhausted.</exception>
    Task<IReadOnlyList<long>> NextHostRangeAsync(string sequenceKey, int count, CancellationToken ct = default);

    /// <summary>Idempotently seeds a HOST-LEVEL sequence.</summary>
    /// <param name="sequenceKey">The sequence key.</param>
    /// <param name="startValue">First value returned. Ignored if the row exists.</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="ArgumentException">
    /// <paramref name="sequenceKey"/> is null, empty, or exceeds the 100-character column limit.
    /// </exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="startValue"/> is not positive.</exception>
    Task EnsureHostSequenceAsync(string sequenceKey, long startValue = 1, CancellationToken ct = default);
}
