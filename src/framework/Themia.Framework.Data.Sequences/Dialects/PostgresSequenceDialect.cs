using System.Data.Common;

using Npgsql;

namespace Themia.Framework.Data.Sequences.Dialects;

/// <summary>PostgreSQL dialect for the sequence allocator.</summary>
internal sealed class PostgresSequenceDialect : ISequenceDialect
{
    /// <inheritdoc />
    public DbConnection CreateConnection(string connectionString)
    {
        // Enlist=false: the allocation must not join a caller's System.Transactions scope, or a rollback
        // there would take the allocated number back and it would be reissued to the next caller.
        var builder = new NpgsqlConnectionStringBuilder(connectionString) { Enlist = false };
        return new NpgsqlConnection(builder.ConnectionString);
    }

    /// <inheritdoc />
    public string SelectForUpdateSql =>
        "SELECT next_value FROM themia_sequences WHERE tenant_id = @tenant AND sequence_key = @key FOR UPDATE";

    /// <inheritdoc />
    public string UpdateNextValueSql =>
        "UPDATE themia_sequences SET next_value = @val WHERE tenant_id = @tenant AND sequence_key = @key";

    /// <inheritdoc />
    public string InsertIfMissingSql =>
        "INSERT INTO themia_sequences (tenant_id, sequence_key, next_value) VALUES (@tenant, @key, @val) "
        + "ON CONFLICT (tenant_id, sequence_key) DO NOTHING";
}
