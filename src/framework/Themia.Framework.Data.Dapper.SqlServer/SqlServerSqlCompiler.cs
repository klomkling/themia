using SqlKata;
using SqlKata.Compilers;
using Themia.Framework.Data.Dapper.Sql;

namespace Themia.Framework.Data.Dapper.SqlServer;

internal sealed class SqlServerSqlCompiler : ISqlCompiler
{
    // UseLegacyPagination=false -> OFFSET/FETCH paging. Set explicitly rather than relying on the SqlKata default.
    private readonly SqlServerCompiler _compiler = new() { UseLegacyPagination = false };

    public CompiledSql Compile(Query query)
    {
        var r = _compiler.Compile(query);
        return new CompiledSql(r.Sql, r.NamedBindings);
    }
}
