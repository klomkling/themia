using System.Data;
using System.Data.Common;
using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Framework.Data.Dapper.Connection;
using Themia.Framework.Data.Dapper.Connections;
using Themia.Framework.Data.Dapper.UnitOfWork;
using Xunit;

namespace Themia.Framework.Data.Dapper.Tests.Connections;

/// <summary>
/// <see cref="DapperAmbientConnectionAccessor"/> must report the honest state of the ambient
/// transaction: null with nothing open, and — when a transaction is open — the very connection and
/// transaction instance the unit of work is using, not merely a non-null placeholder.
/// </summary>
public sealed class DapperAmbientConnectionAccessorTests
{
    [Fact]
    public async Task Returns_null_when_no_transaction_is_open()
    {
        var accessor = new DapperAmbientConnectionAccessor(new FakeConnectionContext());

        Assert.Null(await accessor.TryGetAsync());
    }

    [Fact]
    public async Task Returns_the_ambient_connection_and_transaction_inside_ExecuteInTransactionAsync()
    {
        var context = new FakeConnectionContext();
        var accessor = new DapperAmbientConnectionAccessor(context);
        var unitOfWork = new DapperUnitOfWork(
            context, registry: null!, compiler: null!, tenantContext: null!, currentUser: null!,
            filterScope: new DataFilterScope(), timeProvider: TimeProvider.System, sqlExceptionInterpreter: null!);

        DbTransaction? expectedTransaction = null;
        (DbConnection Connection, DbTransaction Transaction)? seen = null;
        await unitOfWork.ExecuteInTransactionAsync(async ct =>
        {
            expectedTransaction = context.CurrentTransaction;
            seen = await accessor.TryGetAsync(ct);
        });

        Assert.NotNull(seen);
        Assert.Equal(ConnectionState.Open, seen!.Value.Connection.State);
        Assert.NotNull(expectedTransaction);
        Assert.Same(expectedTransaction, seen.Value.Transaction);
    }

    // A minimal IDapperConnectionContext double: enough to open a connection and begin/end a transaction
    // on it, with no real ADO.NET provider involved.
    private sealed class FakeConnectionContext : IDapperConnectionContext
    {
        private readonly FakeConnection connection = new();

        public DbTransaction? CurrentTransaction { get; private set; }

        public Task<DbConnection> GetOpenConnectionAsync(CancellationToken cancellationToken)
        {
            connection.Open();
            return Task.FromResult<DbConnection>(connection);
        }

        public async Task<DbTransaction> BeginTransactionAsync(CancellationToken cancellationToken)
        {
            var conn = await GetOpenConnectionAsync(cancellationToken);
            CurrentTransaction = new FakeTransaction(conn);
            return CurrentTransaction;
        }

        public ValueTask DisposeTransactionAsync()
        {
            CurrentTransaction = null;
            return ValueTask.CompletedTask;
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private sealed class FakeConnection : DbConnection
    {
        private ConnectionState connectionState = ConnectionState.Closed;

        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;

        public override string Database => string.Empty;
        public override string DataSource => string.Empty;
        public override string ServerVersion => string.Empty;
        public override ConnectionState State => connectionState;

        public override void ChangeDatabase(string databaseName) { }
        public override void Close() => connectionState = ConnectionState.Closed;
        public override void Open() => connectionState = ConnectionState.Open;
        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
    }

    private sealed class FakeTransaction(DbConnection connection) : DbTransaction
    {
        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;
        protected override DbConnection DbConnection => connection;

        public override void Commit() { }
        public override void Rollback() { }
        public override Task CommitAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
        public override Task RollbackAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
