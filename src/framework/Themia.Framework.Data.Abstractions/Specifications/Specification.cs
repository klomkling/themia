using System.Linq.Expressions;

namespace Themia.Framework.Data.Abstractions.Specifications;

/// <summary>Fluent base for building an <see cref="ISpecification{T}"/>.</summary>
public abstract class Specification<T> : ISpecification<T>
{
    private readonly List<OrderExpression<T>> orderBy = [];

    /// <inheritdoc />
    public Expression<Func<T, bool>>? Criteria { get; private set; }
    /// <inheritdoc />
    IReadOnlyList<OrderExpression<T>> ISpecification<T>.OrderBy => orderBy;
    /// <inheritdoc />
    public int? Skip { get; private set; }
    /// <inheritdoc />
    public int? Take { get; private set; }
    /// <inheritdoc />
    public bool IgnoreTenantFilter { get; private set; }

    /// <summary>Adds a filter predicate (ANDed with any existing criteria).</summary>
    protected Specification<T> Where(Expression<Func<T, bool>> criteria)
    {
        Criteria = Criteria is null ? criteria : Criteria.AndAlso(criteria);
        return this;
    }

    /// <summary>Adds an ordering term.</summary>
    protected Specification<T> AddOrderBy(Expression<Func<T, object?>> keySelector, bool descending)
    {
        orderBy.Add(new OrderExpression<T>(keySelector, descending));
        return this;
    }

    /// <summary>Adds an ascending ordering term.</summary>
    /// <remarks>
    /// Intentionally shares its name with the explicitly-implemented <see cref="ISpecification{T}.OrderBy"/>
    /// property (the accumulated ordering list). On a <see cref="Specification{T}"/> reference this name binds
    /// to this fluent method; cast to <see cref="ISpecification{T}"/> to read the ordering list. Do not "merge" them.
    /// </remarks>
    public Specification<T> OrderBy(Expression<Func<T, object?>> keySelector) => AddOrderBy(keySelector, false);

    /// <summary>Adds a descending ordering term.</summary>
    public Specification<T> OrderByDescending(Expression<Func<T, object?>> keySelector) => AddOrderBy(keySelector, true);

    /// <summary>Sets paging (skip/take).</summary>
    public Specification<T> Page(int? skip, int? take) { Skip = skip; Take = take; return this; }

    /// <summary>Opts this specification out of the tenant filter (deliberate cross-tenant access).</summary>
    public Specification<T> WithoutTenantFilter() { IgnoreTenantFilter = true; return this; }
}
