namespace Themia.Framework.Data.Dapper.Sql;

/// <summary>A compiled SQL statement and its named parameter values (ready for Dapper).</summary>
public sealed record CompiledSql(string Sql, IReadOnlyDictionary<string, object?> Parameters);
