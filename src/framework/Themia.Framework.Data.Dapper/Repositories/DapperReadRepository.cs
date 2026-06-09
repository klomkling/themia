using global::Dapper;
using SqlKata;
using Themia.Framework.Data.Abstractions.Paging;
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.Specifications;
using Themia.Framework.Data.Dapper.Connection;
using Themia.Framework.Data.Dapper.Mapping;
using Themia.Framework.Data.Dapper.Sql;
using Themia.Framework.Data.Dapper.Tenancy;
using Themia.Framework.Data.Dapper.Translation;

namespace Themia.Framework.Data.Dapper.Repositories;

internal class DapperReadRepository<T, TKey>(
    IDapperConnectionContext connection,
    ITenantQueryFactory queryFactory,
    EntityMappingRegistry registry,
    ISqlCompiler compiler) : IReadRepository<T, TKey> where T : class
{
    protected readonly IDapperConnectionContext Connection = connection;
    protected readonly EntityMappingRegistry Registry = registry;
    protected readonly ISqlCompiler Compiler = compiler;

    protected EntityMapping Map => Registry.For<T>();

    private Query Seeded() => queryFactory.For<T>();

    public async Task<T?> GetByIdAsync(TKey id, CancellationToken cancellationToken = default)
        => await QuerySingleAsync(Seeded().Where(Map.KeyColumn, id).Limit(1), cancellationToken);

    public async Task<IReadOnlyList<T>> ListAsync(ISpecification<T> spec, CancellationToken cancellationToken = default)
    {
        var query = Seeded();
        SpecificationTranslator.Apply(query, spec, Map);
        var sql = Compiler.Compile(query);
        var conn = await Connection.GetOpenConnectionAsync(cancellationToken);
        var rows = await conn.QueryAsync<T>(new CommandDefinition(sql.Sql, sql.Parameters, Connection.CurrentTransaction, cancellationToken: cancellationToken));
        return rows.AsList();
    }

    public async Task<T?> FirstOrDefaultAsync(ISpecification<T> spec, CancellationToken cancellationToken = default)
    {
        var query = Seeded();
        SpecificationTranslator.Apply(query, spec, Map);
        return await QuerySingleAsync(query.Limit(1), cancellationToken);
    }

    public async Task<long> CountAsync(ISpecification<T> spec, CancellationToken cancellationToken = default)
    {
        var query = Seeded();
        if (spec.Criteria is not null)
            SpecificationTranslator.Apply(query, OnlyCriteria(spec), Map);
        var sql = Compiler.Compile(query.AsCount());
        var conn = await Connection.GetOpenConnectionAsync(cancellationToken);
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition(sql.Sql, sql.Parameters, Connection.CurrentTransaction, cancellationToken: cancellationToken));
    }

    public async Task<bool> AnyAsync(ISpecification<T> spec, CancellationToken cancellationToken = default)
        => await CountAsync(spec, cancellationToken) > 0;

    public async Task<PagedResult<T>> PageAsync(ISpecification<T> spec, CancellationToken cancellationToken = default)
    {
        var total = await CountAsync(spec, cancellationToken);
        var items = await ListAsync(spec, cancellationToken);
        return new PagedResult<T>(items, total, spec.Skip, spec.Take);
    }

    private async Task<T?> QuerySingleAsync(Query query, CancellationToken cancellationToken)
    {
        var sql = Compiler.Compile(query);
        var conn = await Connection.GetOpenConnectionAsync(cancellationToken);
        return await conn.QueryFirstOrDefaultAsync<T>(new CommandDefinition(sql.Sql, sql.Parameters, Connection.CurrentTransaction, cancellationToken: cancellationToken));
    }

    // Count must ignore paging/order — wrap the spec to expose ONLY its Criteria.
    private static ISpecification<T> OnlyCriteria(ISpecification<T> spec) => new CriteriaOnlySpec(spec);

    private sealed class CriteriaOnlySpec(ISpecification<T> inner) : ISpecification<T>
    {
        public System.Linq.Expressions.Expression<System.Func<T, bool>>? Criteria => inner.Criteria;
        public IReadOnlyList<OrderExpression<T>> OrderBy => System.Array.Empty<OrderExpression<T>>();
        public int? Skip => null;
        public int? Take => null;
        public bool IgnoreTenantFilter => inner.IgnoreTenantFilter;
    }
}
