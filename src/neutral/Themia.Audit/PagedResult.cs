namespace Themia.Audit;

/// <summary>
/// A page of results plus the total matching count. <c>Themia.Audit</c> is a neutral core and ships its
/// own <see cref="PagedResult{T}"/> rather than depending on
/// <c>Themia.Framework.Data.Abstractions.Paging.PagedResult</c> — the same reason
/// <c>Themia.Exceptional</c> ships one. Do not "unify" these two; the layering forbids it.
/// </summary>
/// <typeparam name="T">The item type.</typeparam>
public sealed class PagedResult<T>
{
    /// <summary>The items on this page.</summary>
    public IReadOnlyList<T> Items { get; init; } = Array.Empty<T>();

    /// <summary>The total number of rows matching the filter, ignoring paging.</summary>
    public int Total { get; init; }
}
