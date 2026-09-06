using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;

namespace Themia.Audit.AspNetCore.Tests;

/// <summary>Fake <see cref="IAuditDialect"/> for endpoint tests. <see cref="CreateConnection"/> returns a
/// <see cref="StubDbConnection"/> that <see cref="FakeAuditStore"/> never actually issues SQL through —
/// no test in this project starts a real database connection.</summary>
internal sealed class FakeAuditDialect : IAuditDialect
{
    public DbConnection CreateConnection(string connectionString) => new StubDbConnection();

    public string InsertSql => throw new NotSupportedException("FakeAuditStore never issues SQL through the dialect.");

    public string SelectPageSql => throw new NotSupportedException("FakeAuditStore never issues SQL through the dialect.");

    public string CountSql => throw new NotSupportedException("FakeAuditStore never issues SQL through the dialect.");

    public string PurgeSql => throw new NotSupportedException("FakeAuditStore never issues SQL through the dialect.");
}

/// <summary>Inert <see cref="DbConnection"/>: <see cref="Open"/>/<see cref="OpenAsync(CancellationToken)"/>
/// succeed without touching anything. Exists only because <see cref="IAuditDialect.CreateConnection"/> must
/// return a connection object; <see cref="FakeAuditStore"/> never creates a command or transaction on it.</summary>
internal sealed class StubDbConnection : DbConnection
{
    private ConnectionState state = ConnectionState.Closed;
    private string connectionString = string.Empty;

    [AllowNull]
    public override string ConnectionString
    {
        get => connectionString;
        set => connectionString = value ?? string.Empty;
    }

    public override string Database => string.Empty;

    public override string DataSource => string.Empty;

    public override string ServerVersion => string.Empty;

    public override ConnectionState State => state;

    public override void ChangeDatabase(string databaseName)
    {
    }

    public override void Close() => state = ConnectionState.Closed;

    public override void Open() => state = ConnectionState.Open;

    public override Task OpenAsync(CancellationToken cancellationToken)
    {
        state = ConnectionState.Open;
        return Task.CompletedTask;
    }

    protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
        throw new NotSupportedException("FakeAuditStore never begins a transaction.");

    protected override DbCommand CreateDbCommand() =>
        throw new NotSupportedException("FakeAuditStore never creates a command.");
}
