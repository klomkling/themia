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
    /// <remarks>
    /// Not <c>INSERT IGNORE</c> — rejected here for the same reason it was rejected in
    /// <c>MySqlChallengeDialect</c> and <c>MySqlInboxAdmission</c>: <c>IGNORE</c> downgrades a whole class
    /// of errors to warnings (duplicate key, data truncation, <c>NULL</c> into a <c>NOT NULL</c> column,
    /// out-of-range values), regardless of <c>sql_mode</c> — not just the duplicate-key case this seed
    /// needs to swallow. <c>sequence_key</c> is <c>varchar(100)</c> and nothing upstream of this dialect
    /// enforced that length, so an over-length key would have been silently truncated into the wrong
    /// bucket instead of rejected. <c>ON DUPLICATE KEY UPDATE next_value = next_value</c> is a genuine
    /// no-op on collision (assigning a column its own value changes nothing) but fires only on the actual
    /// duplicate-key violation, leaving truncation and NOT NULL protection intact.
    /// <para>
    /// The affected-row-count / <c>UseAffectedRows</c> trap <c>MySqlInboxAdmission</c> documents does not
    /// apply here: <c>EnsureSequenceAsync</c> returns <see cref="System.Threading.Tasks.Task"/>, not a row
    /// count, so there is nothing for that flag to make ambiguous.
    /// </para>
    /// </remarks>
    public string InsertIfMissingSql =>
        "INSERT INTO themia_sequences (tenant_id, sequence_key, next_value) VALUES (@tenant, @key, @val) "
        + "ON DUPLICATE KEY UPDATE next_value = next_value";
}
