using System.Data.Common;

using MySqlConnector;

namespace Themia.Framework.Data.Sequences.Dialects;

/// <summary>MySQL 8.0.13+ dialect for the sequence allocator. MariaDB is not supported.</summary>
internal sealed class MySqlSequenceDialect : ISequenceDialect
{
    /// <inheritdoc />
    public DbConnection CreateConnection(string connectionString)
    {
        // AutoEnlist=false, NOT UseXaTransactions=false. UseXaTransactions only picks the MECHANISM
        // (XA versus local) MySqlConnector uses once it has already enlisted; AutoEnlist is what stops it
        // enlisting at all, and it defaults to true. Same reason as the other two dialects: joining a
        // caller's ambient System.Transactions scope would let their rollback take the allocated number
        // back, and the next caller would be handed it again.
        var builder = new MySqlConnectionStringBuilder(connectionString) { AutoEnlist = false };
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
