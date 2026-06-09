using SqlKata;

namespace Themia.Framework.Data.Dapper.Tenancy;

/// <summary>
/// Tier-2 entry point: a SqlKata <see cref="Query"/> pre-seeded with the tenant predicate +
/// soft-delete filter. Compose joins/filters on it, then execute via Dapper.
/// </summary>
public interface ITenantQueryFactory
{
    /// <summary>Returns a tenant- and soft-delete-seeded query for <typeparamref name="T"/>.</summary>
    Query For<T>();
}
