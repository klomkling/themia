using System.Linq.Expressions;

namespace Themia.Framework.Data.Abstractions.Specifications;

/// <summary>Combinators that produce a new specification from an existing one plus an extra predicate.</summary>
public static class SpecificationExtensions
{
    /// <summary>Returns a spec whose criteria is the existing criteria ANDed with <paramref name="extra"/>.</summary>
    public static ISpecification<T> And<T>(this ISpecification<T> spec, Expression<Func<T, bool>> extra) =>
        new CombinedSpecification<T>(spec, spec.Criteria is null ? extra : spec.Criteria.AndAlso(extra));

    /// <summary>Returns a spec whose criteria is the existing criteria ORed with <paramref name="extra"/>.</summary>
    public static ISpecification<T> Or<T>(this ISpecification<T> spec, Expression<Func<T, bool>> extra) =>
        new CombinedSpecification<T>(spec, spec.Criteria is null ? extra : spec.Criteria.OrElse(extra));

    /// <summary>Returns a spec whose criteria is the negation of the existing criteria.</summary>
    public static ISpecification<T> Not<T>(this ISpecification<T> spec)
    {
        if (spec.Criteria is null) return spec;
        var param = spec.Criteria.Parameters[0];
        var negated = Expression.Lambda<Func<T, bool>>(Expression.Not(spec.Criteria.Body), param);
        return new CombinedSpecification<T>(spec, negated);
    }

    internal static Expression<Func<T, bool>> AndAlso<T>(this Expression<Func<T, bool>> left, Expression<Func<T, bool>> right)
        => Combine(left, right, Expression.AndAlso);

    internal static Expression<Func<T, bool>> OrElse<T>(this Expression<Func<T, bool>> left, Expression<Func<T, bool>> right)
        => Combine(left, right, Expression.OrElse);

    private static Expression<Func<T, bool>> Combine<T>(
        Expression<Func<T, bool>> left,
        Expression<Func<T, bool>> right,
        Func<Expression, Expression, BinaryExpression> op)
    {
        var param = Expression.Parameter(typeof(T), "x");
        var l = new ReplaceParameterVisitor(left.Parameters[0], param).Visit(left.Body)!;
        var r = new ReplaceParameterVisitor(right.Parameters[0], param).Visit(right.Body)!;
        return Expression.Lambda<Func<T, bool>>(op(l, r), param);
    }

    private sealed class ReplaceParameterVisitor(ParameterExpression from, ParameterExpression to) : ExpressionVisitor
    {
        protected override Expression VisitParameter(ParameterExpression node) =>
            node == from ? to : base.VisitParameter(node);
    }
}

// Wraps an existing spec but overrides its Criteria; preserves OrderBy/paging/tenant flag.
internal sealed class CombinedSpecification<T> : ISpecification<T>
{
    private readonly ISpecification<T> inner;

    public CombinedSpecification(ISpecification<T> inner, Expression<Func<T, bool>> criteria)
    {
        this.inner = inner;
        Criteria = criteria;
    }

    public Expression<Func<T, bool>>? Criteria { get; }
    public IReadOnlyList<OrderExpression<T>> OrderBy => inner.OrderBy;
    public int? Skip => inner.Skip;
    public int? Take => inner.Take;
    public bool IgnoreTenantFilter => inner.IgnoreTenantFilter;
}
