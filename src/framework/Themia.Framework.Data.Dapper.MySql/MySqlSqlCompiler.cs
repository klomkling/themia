using SqlKata;
using SqlKata.Compilers;
using Themia.Framework.Data.Dapper.Sql;

namespace Themia.Framework.Data.Dapper.MySql;

internal sealed class MySqlSqlCompiler : ISqlCompiler
{
    private readonly MySqlCompiler _compiler = new();

    public CompiledSql Compile(Query query)
    {
        var r = _compiler.Compile(query);
        return new CompiledSql(r.Sql, r.NamedBindings);
    }
}
