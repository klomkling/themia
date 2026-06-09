using SqlKata;
using SqlKata.Compilers;
using Themia.Framework.Data.Dapper.Sql;

namespace Themia.Framework.Data.Dapper.PostgreSql;

internal sealed class PostgresSqlCompiler : ISqlCompiler
{
    private readonly NativeReturningPostgresCompiler _compiler = new();

    public CompiledSql Compile(Query query)
    {
        var r = _compiler.Compile(query);
        return new CompiledSql(r.Sql, r.NamedBindings);
    }

    /// <summary>
    /// PostgresCompiler generates "INSERT ... ; SELECT lastval() AS id" for store-generated keys.
    /// PostgreSQL's native form is "INSERT ... RETURNING id", which works for both sequence-backed
    /// integer PKs and UUID columns with a DEFAULT expression (e.g. gen_random_uuid()).
    /// The returned column is always literally "id"; <c>DapperUnitOfWork</c> enforces this precondition by
    /// rejecting a store-generated key on a column-overridden key (e.g. Id -> "category_id") before the
    /// insert reaches this rewrite, so the hard-coded "id" here is safe.
    /// </summary>
    private sealed class NativeReturningPostgresCompiler : PostgresCompiler
    {
        private const string LastValSuffix = ";SELECT lastval() AS id";

        public override SqlResult Compile(Query query)
        {
            var result = base.Compile(query);
            if (result.Sql.EndsWith(LastValSuffix, StringComparison.Ordinal))
                result.Sql = result.Sql[..^LastValSuffix.Length] + " RETURNING id";
            return result;
        }
    }
}
