namespace Themia.Exceptional;

/// <summary>A page of results plus the total matching count.</summary>
/// <typeparam name="T">Item type.</typeparam>
public sealed class PagedResult<T>
{
    /// <summary>Items on this page.</summary>
    public IReadOnlyList<T> Items { get; init; } = Array.Empty<T>();

    /// <summary>Total number of rows matching the filter (ignoring paging).</summary>
    public int Total { get; init; }
}
