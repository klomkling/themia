using SqlKata;
using SqlKata.Compilers;
using Themia.Framework.Data.Dapper.Sql;

namespace Themia.Framework.Data.Dapper.PostgreSql;

internal sealed class PostgresSqlCompiler : ISqlCompiler
{
    private readonly PostgresCompiler _compiler = new();

    public CompiledSql Compile(Query query)
    {
        var r = _compiler.Compile(query);
        return new CompiledSql(r.Sql, r.NamedBindings);
    }
}
