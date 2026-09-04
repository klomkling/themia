using System.Data.Common;

using MySqlConnector;

namespace Themia.Framework.Data.Sequences.Dialects;

/// <summary>MySQL 8.0.13+ dialect for the sequence allocator. MariaDB is not supported.</summary>
internal sealed class MySqlSequenceDialect : ISequenceDialect
{
    /// <inheritdoc />
    public DbConnection CreateConnection(string connectionString)
    {
        var builder = new MySqlConnectionStringBuilder(connectionString) { UseXaTransactions = false };
        return new MySqlConnection(builder.ConnectionString);
    }

    /// <inheritdoc />
    public string SelectForUpdateSql =>
        "SELECT next_value FROM themia_sequences WHERE tenant_id = @tenant AND sequence_key = @key FOR UPDATE";

    /// <inheritdoc />
    public string UpdateNextValueSql =>
        "UPDATE themia_sequences SET next_value = @val WHERE tenant_id = @tenant AND sequence_key = @key";

    /// <inheritdoc />
    public string InsertIfMissingSql =>
        "INSERT IGNORE INTO themia_sequences (tenant_id, sequence_key, next_value) VALUES (@tenant, @key, @val)";
}
