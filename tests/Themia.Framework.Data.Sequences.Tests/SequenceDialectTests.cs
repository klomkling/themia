using Microsoft.Data.SqlClient;

using MySqlConnector;

using Npgsql;

using Themia.Framework.Data.Sequences;
using Themia.Framework.Data.Sequences.Dialects;
using Xunit;

namespace Themia.Framework.Data.Sequences.Tests;

public sealed class SequenceDialectTests
{
    [Theory]
    [InlineData(SequenceEngine.Postgres, "Npgsql")]
    [InlineData(SequenceEngine.MySql, "MySqlConnector")]
    [InlineData(SequenceEngine.SqlServer, "Microsoft.Data.SqlClient")]
    public void Factory_ReturnsTheEngineSpecificDialect(SequenceEngine engine, string expectedConnectionNamespace)
    {
        var dialect = SequenceDialectFactory.For(engine);

        using var connection = dialect.CreateConnection(ConnectionStringFor(engine));
        Assert.StartsWith(expectedConnectionNamespace, connection.GetType().Namespace, StringComparison.Ordinal);
    }

    [Fact]
    public void Factory_RejectsAnUndefinedEngine()
        => Assert.Throws<NotSupportedException>(() => SequenceDialectFactory.For((SequenceEngine)99));

    // Every dialect locks the row it is about to advance. Without the lock two callers read the same
    // NextValue and both return it -- the duplicate this package exists to prevent.
    [Theory]
    [InlineData(SequenceEngine.Postgres, "FOR UPDATE")]
    [InlineData(SequenceEngine.MySql, "FOR UPDATE")]
    [InlineData(SequenceEngine.SqlServer, "UPDLOCK")]
    public void SelectForUpdate_TakesARowLock(SequenceEngine engine, string lockClause)
        => Assert.Contains(lockClause, SequenceDialectFactory.For(engine).SelectForUpdateSql, StringComparison.OrdinalIgnoreCase);

    // Seeding must be a single atomic statement. The naive "SELECT then INSERT" races: two callers both
    // see no row, both insert, the second gets a primary-key violation.
    [Theory]
    [InlineData(SequenceEngine.Postgres, "ON CONFLICT")]
    [InlineData(SequenceEngine.MySql, "ON DUPLICATE KEY UPDATE")]
    [InlineData(SequenceEngine.SqlServer, "WHERE NOT EXISTS")]
    public void InsertIfMissing_IsAtomic(SequenceEngine engine, string marker)
        => Assert.Contains(marker, SequenceDialectFactory.For(engine).InsertIfMissingSql, StringComparison.OrdinalIgnoreCase);

    [Theory]
    [InlineData(SequenceEngine.Postgres)]
    [InlineData(SequenceEngine.MySql)]
    [InlineData(SequenceEngine.SqlServer)]
    public void EveryStatement_KeysOnBothTenantAndSequenceKey(SequenceEngine engine)
    {
        // The primary key is (tenant_id, sequence_key). A statement that filtered on sequence_key alone
        // would read or advance another tenant's counter, which no test of a single tenant would catch.
        var dialect = SequenceDialectFactory.For(engine);

        foreach (var sql in new[] { dialect.SelectForUpdateSql, dialect.UpdateNextValueSql, dialect.InsertIfMissingSql })
        {
            Assert.Contains("@tenant", sql, StringComparison.Ordinal);
            Assert.Contains("@key", sql, StringComparison.Ordinal);
        }
    }

    // Enlistment suppression is the mechanism that prevents the caller's ambient System.Transactions rollback
    // from taking the allocated sequence number back. Without it, the caller's rollback would cause the next
    // caller to be handed the same number again — the duplicate this package exists to prevent.
    [Fact]
    public void PostgresDialect_SuppressesEnlistment()
    {
        var dialect = SequenceDialectFactory.For(SequenceEngine.Postgres);
        using var connection = dialect.CreateConnection(ConnectionStringFor(SequenceEngine.Postgres));

        var builder = new NpgsqlConnectionStringBuilder(connection.ConnectionString);
        Assert.False(builder.Enlist);
    }

    [Fact]
    public void MySqlDialect_SuppressesEnlistment()
    {
        var dialect = SequenceDialectFactory.For(SequenceEngine.MySql);
        using var connection = dialect.CreateConnection(ConnectionStringFor(SequenceEngine.MySql));

        var builder = new MySqlConnectionStringBuilder(connection.ConnectionString);
        Assert.False(builder.AutoEnlist);
    }

    [Fact]
    public void SqlServerDialect_SuppressesEnlistment()
    {
        var dialect = SequenceDialectFactory.For(SequenceEngine.SqlServer);
        using var connection = dialect.CreateConnection(ConnectionStringFor(SequenceEngine.SqlServer));

        var builder = new SqlConnectionStringBuilder(connection.ConnectionString);
        Assert.False(builder.Enlist);
    }

    private static string ConnectionStringFor(SequenceEngine engine) => engine switch
    {
        SequenceEngine.Postgres => "Host=localhost;Database=x;Username=u;Password=p",
        SequenceEngine.MySql => "Server=localhost;Database=x;Uid=u;Pwd=p",
        SequenceEngine.SqlServer => "Server=localhost;Database=x;User Id=u;Password=p;TrustServerCertificate=true",
        _ => throw new NotSupportedException(),
    };
}
