using SqlKata;

namespace Themia.Framework.Data.Dapper.Tenancy;

/// <summary>
/// Tier-2 entry point: a SqlKata <see cref="Query"/> for <typeparamref name="T"/> pre-seeded with the
/// tenant predicate + soft-delete filter. Compose joins/filters on it, then execute via Dapper.
/// </summary>
public interface ITenantQueryFactory
{
    /// <summary>Returns a tenant- and soft-delete-seeded query for the entity.</summary>
    Query For<T>();
}
