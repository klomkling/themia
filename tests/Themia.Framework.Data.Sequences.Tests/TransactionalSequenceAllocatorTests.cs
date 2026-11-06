using System.Data.Common;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Sequences;
using Themia.Framework.Data.Sequences.Dialects;
using Xunit;

namespace Themia.Framework.Data.Sequences.Tests;

public sealed class TransactionalSequenceAllocatorTests
{
    private static TransactionalSequenceAllocator CreateAllocator(
        string? tenantId = "acme",
        SequenceEngine engine = SequenceEngine.Postgres)
    {
        var options = new SequenceOptions
        {
            ConnectionString = "Host=localhost;Database=test;Username=postgres;Password=postgres",
            Engine = engine
        };
        var tenantContext = new TenantContext(tenantId is null ? null : new TenantId(tenantId));
        return new TransactionalSequenceAllocator(options, tenantContext);
    }

    [Fact]
    public async Task NextInTransactionAsync_WithNullTransaction_ThrowsArgumentNullException()
    {
        var allocator = CreateAllocator();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => allocator.NextInTransactionAsync(null!, "DocNo:TaxInvoice:2026-10"));
    }

    [Fact]
    public async Task NextHostInTransactionAsync_WithNullTransaction_ThrowsArgumentNullException()
    {
        var allocator = CreateAllocator();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => allocator.NextHostInTransactionAsync(null!, "DocNo:TaxInvoice:2026-10"));
    }

    [Fact]
    public async Task NextInTransactionAsync_WithNullKey_ThrowsArgumentNullException()
    {
        var allocator = CreateAllocator();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => allocator.NextInTransactionAsync(new FakeDbTransaction(), null!));
    }

    [Fact]
    public async Task NextInTransactionAsync_WithEmptyKey_ThrowsArgumentException()
    {
        var allocator = CreateAllocator();

        await Assert.ThrowsAsync<ArgumentException>(
            () => allocator.NextInTransactionAsync(new FakeDbTransaction(), string.Empty));
    }

    [Fact]
    public async Task NextHostInTransactionAsync_WithNullKey_ThrowsArgumentNullException()
    {
        var allocator = CreateAllocator();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => allocator.NextHostInTransactionAsync(new FakeDbTransaction(), null!));
    }

    [Fact]
    public async Task NextHostInTransactionAsync_WithEmptyKey_ThrowsArgumentException()
    {
        var allocator = CreateAllocator();

        await Assert.ThrowsAsync<ArgumentException>(
            () => allocator.NextHostInTransactionAsync(new FakeDbTransaction(), string.Empty));
    }

    [Fact]
    public async Task NextInTransactionAsync_WithOverLengthKey_ThrowsArgumentException()
    {
        var allocator = CreateAllocator();
        var overLengthKey = new string('x', SequenceProvider.MaxSequenceKeyLength + 1);

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => allocator.NextInTransactionAsync(new FakeDbTransaction(), overLengthKey));

        Assert.Contains("exceeding", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NextHostInTransactionAsync_WithOverLengthKey_ThrowsArgumentException()
    {
        var allocator = CreateAllocator();
        var overLengthKey = new string('x', SequenceProvider.MaxSequenceKeyLength + 1);

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => allocator.NextHostInTransactionAsync(new FakeDbTransaction(), overLengthKey));

        Assert.Contains("exceeding", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NextInTransactionAsync_WithNoAmbientTenant_ThrowsInvalidOperationException()
    {
        var allocator = CreateAllocator(tenantId: null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => allocator.NextInTransactionAsync(new FakeDbTransaction(), "DocNo:TaxInvoice:2026-10"));

        Assert.Contains("no ambient tenant", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NextInTransactionAsync_WhenTransactionHasNoConnection_ThrowsInvalidOperationException()
    {
        var allocator = CreateAllocator();
        var txWithoutConnection = new FakeDbTransaction(connection: null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => allocator.NextInTransactionAsync(txWithoutConnection, "DocNo:TaxInvoice:2026-10"));

        Assert.Contains("no associated connection", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NextHostInTransactionAsync_WhenTransactionHasNoConnection_ThrowsInvalidOperationException()
    {
        var allocator = CreateAllocator();
        var txWithoutConnection = new FakeDbTransaction(connection: null);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => allocator.NextHostInTransactionAsync(txWithoutConnection, "DocNo:TaxInvoice:2026-10"));

        Assert.Contains("no associated connection", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task NextInTransactionAsync_WithMySqlEngineAndWrappedConnection_SeedsInsideTheCallerTransaction()
    {
        // A profiler or wrapper connection is not a MySqlConnection, so no side connection can be cloned from it;
        // the allocator must keep today's in-transaction seed instead of failing with InvalidCastException.
        var connection = new RecordingConnection();
        var allocator = CreateAllocator(engine: SequenceEngine.MySql);

        await Assert.ThrowsAsync<StopAfterSeedException>(
            () => allocator.NextInTransactionAsync(new FakeDbTransaction(connection), "DocNo:TaxInvoice:2026-10"));

        var seed = Assert.Single(connection.Commands);
        Assert.Contains("ON DUPLICATE KEY UPDATE", seed, StringComparison.Ordinal);
    }

    private sealed class StopAfterSeedException : Exception;

    private sealed class RecordingConnection : DbConnection
    {
        public List<string> Commands { get; } = [];

        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;
        public override string Database => "test";
        public override string DataSource => "test";
        public override string ServerVersion => "0";
        public override System.Data.ConnectionState State => System.Data.ConnectionState.Open;
        public override void ChangeDatabase(string databaseName) { }
        public override void Close() { }
        public override void Open() { }
        protected override DbTransaction BeginDbTransaction(System.Data.IsolationLevel isolationLevel) =>
            throw new NotSupportedException();
        protected override DbCommand CreateDbCommand() => new RecordingCommand(this);
    }

    private sealed class RecordingCommand(RecordingConnection owner) : DbCommand
    {
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string CommandText { get; set; } = string.Empty;
        public override int CommandTimeout { get; set; }
        public override System.Data.CommandType CommandType { get; set; }
        public override bool DesignTimeVisible { get; set; }
        public override System.Data.UpdateRowSource UpdatedRowSource { get; set; }
        protected override DbConnection? DbConnection { get; set; } = owner;
        protected override DbParameterCollection DbParameterCollection { get; } = new RecordingParameters();
        protected override DbTransaction? DbTransaction { get; set; }
        public override void Cancel() { }
        public override int ExecuteNonQuery()
        {
            owner.Commands.Add(CommandText);
            return 1;
        }
        public override object? ExecuteScalar() => throw new StopAfterSeedException();
        public override void Prepare() { }
        protected override DbParameter CreateDbParameter() => new RecordingParameter();
        protected override DbDataReader ExecuteDbDataReader(System.Data.CommandBehavior behavior) =>
            throw new StopAfterSeedException();
    }

    private sealed class RecordingParameter : DbParameter
    {
        public override System.Data.DbType DbType { get; set; }
        public override System.Data.ParameterDirection Direction { get; set; }
        public override bool IsNullable { get; set; }
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string ParameterName { get; set; } = string.Empty;
        public override int Size { get; set; }
        [System.Diagnostics.CodeAnalysis.AllowNull]
        public override string SourceColumn { get; set; } = string.Empty;
        public override bool SourceColumnNullMapping { get; set; }
        public override object? Value { get; set; }
        public override void ResetDbType() { }
    }

    private sealed class RecordingParameters : DbParameterCollection
    {
        private readonly List<DbParameter> items = [];
        public override int Count => items.Count;
        public override object SyncRoot => items;
        public override int Add(object value) { items.Add((DbParameter)value); return items.Count - 1; }
        public override void AddRange(Array values) { foreach (var v in values) { Add(v!); } }
        public override void Clear() => items.Clear();
        public override bool Contains(object value) => items.Contains((DbParameter)value);
        public override bool Contains(string value) => items.Exists(p => p.ParameterName == value);
        public override void CopyTo(Array array, int index) => ((System.Collections.ICollection)items).CopyTo(array, index);
        public override System.Collections.IEnumerator GetEnumerator() => items.GetEnumerator();
        public override int IndexOf(object value) => items.IndexOf((DbParameter)value);
        public override int IndexOf(string parameterName) => items.FindIndex(p => p.ParameterName == parameterName);
        public override void Insert(int index, object value) => items.Insert(index, (DbParameter)value);
        public override void Remove(object value) => items.Remove((DbParameter)value);
        public override void RemoveAt(int index) => items.RemoveAt(index);
        public override void RemoveAt(string parameterName) => items.RemoveAt(IndexOf(parameterName));
        protected override DbParameter GetParameter(int index) => items[index];
        protected override DbParameter GetParameter(string parameterName) => items[IndexOf(parameterName)];
        protected override void SetParameter(int index, DbParameter value) => items[index] = value;
        protected override void SetParameter(string parameterName, DbParameter value) => items[IndexOf(parameterName)] = value;
    }

    private sealed class FakeDbTransaction(DbConnection? connection = null) : DbTransaction
    {
        public override System.Data.IsolationLevel IsolationLevel => System.Data.IsolationLevel.ReadCommitted;
        protected override DbConnection? DbConnection => connection;
        public override void Commit() { }
        public override void Rollback() { }
    }
}
