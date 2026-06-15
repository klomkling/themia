using System.Linq.Expressions;

namespace Themia.Framework.Data.Abstractions.Repositories;

/// <summary>
/// Fluent column-value collector for a set-based <see cref="IRepository{T,TKey}.UpdateWhereAsync"/>.
/// Each <see cref="Set{TProperty}"/> names one entity property and the constant value to write to its
/// column in the single <c>UPDATE … SET …</c> statement.
/// </summary>
public interface IBulkUpdateSetters<T>
{
    /// <summary>Assigns <paramref name="value"/> to the column backing <paramref name="property"/>.</summary>
    /// <param name="property">A direct property-access expression on the entity (e.g. <c>t =&gt; t.RevokedAt</c>).</param>
    /// <param name="value">The constant value to write.</param>
    /// <returns>The same instance, so calls can be chained.</returns>
    IBulkUpdateSetters<T> Set<TProperty>(Expression<Func<T, TProperty>> property, TProperty value);
}
