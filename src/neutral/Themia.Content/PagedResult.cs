namespace Themia.Content;

/// <summary>One page of a list, and how many items the whole list holds.</summary>
/// <typeparam name="T">The item type.</typeparam>
public sealed class PagedResult<T>
{
    /// <summary>The items on this page.</summary>
    public IReadOnlyList<T> Items { get; init; } = Array.Empty<T>();

    /// <summary>The number of items across every page.</summary>
    public int Total { get; init; }
}
