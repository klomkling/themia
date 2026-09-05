using System.Data.Common;

using Microsoft.Data.SqlClient;

namespace Themia.Framework.Data.Sequences.Dialects;

/// <summary>SQL Server dialect for the sequence allocator.</summary>
internal sealed class SqlServerSequenceDialect : ISequenceDialect
{
    /// <inheritdoc />
    public DbConnection CreateConnection(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString) { Enlist = false };
        return new SqlConnection(builder.ConnectionString);
    }

    /// <inheritdoc />
    public string SelectForUpdateSql =>
        "SELECT next_value FROM themia_sequences WITH (UPDLOCK, HOLDLOCK) "
        + "WHERE tenant_id = @tenant AND sequence_key = @key";

    /// <inheritdoc />
    public string UpdateNextValueSql =>
        "UPDATE themia_sequences SET next_value = @val WHERE tenant_id = @tenant AND sequence_key = @key";

    /// <inheritdoc />
    /// <remarks>
    /// INSERT ... SELECT ... WHERE NOT EXISTS with UPDLOCK/HOLDLOCK on the existence check, not
    /// "IF NOT EXISTS then INSERT" — the latter is two statements and races, and not MERGE, which has
    /// documented concurrency bugs across SQL Server versions.
    /// </remarks>
    public string InsertIfMissingSql =>
        "INSERT INTO themia_sequences (tenant_id, sequence_key, next_value) "
        + "SELECT @tenant, @key, @val WHERE NOT EXISTS ("
        + "SELECT 1 FROM themia_sequences WITH (UPDLOCK, HOLDLOCK) "
        + "WHERE tenant_id = @tenant AND sequence_key = @key)";
}
