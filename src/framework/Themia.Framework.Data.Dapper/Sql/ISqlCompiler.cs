using SqlKata;

namespace Themia.Framework.Data.Dapper.Sql;

/// <summary>Compiles a SqlKata <see cref="Query"/> to engine-specific SQL + parameters. Implemented per
/// engine (PostgresCompiler, etc.). The only place SqlKata's compiler types are referenced.</summary>
public interface ISqlCompiler
{
    /// <summary>Compiles the query to SQL and named parameters.</summary>
    CompiledSql Compile(Query query);
}
