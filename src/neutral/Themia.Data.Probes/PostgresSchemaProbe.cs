using System.Data;

namespace Themia.Data.Probes;

/// <summary>Outcome of probing one table.</summary>
/// <param name="ResolvedSchema">
/// Schema the identifier resolves to through <c>search_path</c>, or <see langword="null"/> when it
/// resolves to nothing.
/// </param>
/// <param name="PublicCopyExists">Whether a table of the same name also exists in <c>public</c>.</param>
public readonly record struct ProbeResult(string? ResolvedSchema, bool PublicCopyExists);

/// <summary>
/// Confirms that a table a Themia store addresses without a schema actually resolves through the
/// connection's <c>search_path</c>. PostgreSQL only.
/// </summary>
public static class PostgresSchemaProbe
{
    // to_regclass returns NULL rather than throwing for an unresolvable name, and resolves names
    // exactly the way the store's own unqualified SQL does -- which is what makes it the right
    // probe rather than a lookup in information_schema.
    private const string Sql = """
        SELECT
          (SELECT n.nspname
             FROM pg_class c
             JOIN pg_namespace n ON n.oid = c.relnamespace
            WHERE c.oid = to_regclass(@name))            AS resolved_schema,
          (to_regclass('public.' || @name) IS NOT NULL)  AS public_copy_exists
        """;

    /// <summary>Probes one table on an open connection.</summary>
    /// <param name="connection">An open PostgreSQL connection.</param>
    /// <param name="tableName">
    /// The identifier exactly as the store's own SQL writes it -- unqualified, quoting included:
    /// <c>data_protection_keys</c>, but <c>"Exceptions"</c>. Every call site passes a compile-time
    /// constant, which is what makes the <c>'public.' || @name</c> concatenation safe.
    /// </param>
    /// <returns>The resolved schema and whether a <c>public</c> copy exists.</returns>
    public static ProbeResult Probe(IDbConnection connection, string tableName)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(tableName);

        using var command = connection.CreateCommand();
        command.CommandText = Sql;

        var parameter = command.CreateParameter();
        parameter.ParameterName = "@name";
        parameter.DbType = DbType.String;
        parameter.Value = tableName;
        command.Parameters.Add(parameter);

        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return new ProbeResult(null, false);
        }

        var schema = reader.IsDBNull(0) ? null : reader.GetString(0);
        var publicCopy = !reader.IsDBNull(1) && reader.GetBoolean(1);
        return new ProbeResult(schema, publicCopy);
    }
}
