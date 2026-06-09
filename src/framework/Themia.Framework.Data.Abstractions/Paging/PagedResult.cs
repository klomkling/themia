namespace Themia.Framework.Data.Abstractions.Paging;

/// <summary>A page of results plus the total count of matching rows (ignoring paging).</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, long Total, int? Skip, int? Take);
