using System.Linq.Expressions;

namespace Themia.Framework.Data.Abstractions.Specifications;

/// <summary>One ORDER BY term: a member selector and its direction.</summary>
public sealed record OrderExpression<T>(Expression<Func<T, object?>> KeySelector, bool Descending);

/// <summary>
/// A provider-agnostic query specification: an optional filter predicate, ordering, paging, and an
/// explicit opt-out of the tenant filter. Translated to LINQ by the EF layer and to SqlKata by the
/// Dapper layer. Single-entity predicates only; joins/projections are provider-native (tier 2).
/// </summary>
public interface ISpecification<T>
{
    /// <summary>The filter predicate, or null for "match all (within tenant)".</summary>
    Expression<Func<T, bool>>? Criteria { get; }
    /// <summary>Ordering terms, applied in order.</summary>
    IReadOnlyList<OrderExpression<T>> OrderBy { get; }
    /// <summary>Rows to skip (paging offset), or null.</summary>
    int? Skip { get; }
    /// <summary>Max rows to take (page size), or null.</summary>
    int? Take { get; }
    /// <summary>When true, the tenant predicate is NOT applied (deliberate cross-tenant/admin access).</summary>
    bool IgnoreTenantFilter { get; }
}
