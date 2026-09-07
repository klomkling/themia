using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Themia.Audit;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Connections;

namespace Themia.Modules.Audit.Tests;

/// <summary>In-memory <see cref="IAuditStore"/>. Filters the same way the real dialect-backed store
/// would, so tenant-scoping tests can assert on the returned rows; records the last connection/query it
/// received for direct assertions.</summary>
internal sealed class FakeAuditStore : IAuditStore
{
    private readonly List<AuditEntry> entries;

    public FakeAuditStore(params AuditEntry[] entries) => this.entries = entries.ToList();

    public AuditEntry? LastWritten { get; private set; }

    public DbConnection? LastWriteConnection { get; private set; }

    public DbTransaction? LastWriteTransaction { get; private set; }

    public AuditQuery? LastQuery { get; private set; }

    /// <summary>When set, every method throws it (simulates a store/connection failure — e.g. a missing table).</summary>
    public Exception? FailWith { get; set; }

    public Task<long> WriteAsync(AuditEntry entry, DbConnection connection, DbTransaction? transaction, CancellationToken cancellationToken)
    {
        if (FailWith is not null) throw FailWith;
        entries.Add(entry);
        LastWritten = entry;
        LastWriteConnection = connection;
        LastWriteTransaction = transaction;
        return Task.FromResult((long)entries.Count);
    }

    public Task<PagedResult<AuditEntry>> QueryAsync(AuditQuery query, DbConnection connection, CancellationToken cancellationToken)
    {
        LastQuery = query;
        if (FailWith is not null) throw FailWith;

        IEnumerable<AuditEntry> filtered = entries;
        if (query.HostLevelOnly) filtered = filtered.Where(e => e.TenantId is null);
        else if (query.TenantId is not null) filtered = filtered.Where(e => e.TenantId == query.TenantId);

        var list = filtered.ToList();
        return Task.FromResult(new PagedResult<AuditEntry> { Items = list, Total = list.Count });
    }

    public Task<AuditEntry?> GetAsync(Guid eventUid, DbConnection connection, CancellationToken cancellationToken) =>
        throw new NotSupportedException();

    public Task<int> PurgeAsync(DateTimeOffset olderThan, DbConnection connection, CancellationToken cancellationToken) =>
        throw new NotSupportedException();
}

/// <summary>Fake <see cref="IAuditDialect"/>. <see cref="CreateConnection"/> returns a
/// <see cref="StubDbConnection"/> that <see cref="FakeAuditStore"/> never issues SQL through.</summary>
internal sealed class FakeAuditDialect : IAuditDialect
{
    public DbConnection CreateConnection(string connectionString) => new StubDbConnection();

    public string InsertSql => throw new NotSupportedException();

    public string SelectPageSql => throw new NotSupportedException();

    public string CountSql => throw new NotSupportedException();

    public string PurgeSql => throw new NotSupportedException();
}

/// <summary>Inert <see cref="DbConnection"/>: open/close succeed without touching anything.</summary>
internal sealed class StubDbConnection : DbConnection
{
    private ConnectionState state = ConnectionState.Closed;

    [AllowNull]
    public override string ConnectionString { get; set; } = string.Empty;

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
        throw new NotSupportedException();

    protected override DbCommand CreateDbCommand() =>
        throw new NotSupportedException();
}

/// <summary>Inert <see cref="DbTransaction"/> standing in for an ambient transaction. Never actually
/// commits/rolls back anything — <see cref="FakeAuditStore"/> never issues SQL through it.</summary>
internal sealed class FakeDbTransaction(DbConnection connection) : DbTransaction
{
    protected override DbConnection DbConnection { get; } = connection;

    public override IsolationLevel IsolationLevel => IsolationLevel.Unspecified;

    public override void Commit()
    {
    }

    public override void Rollback()
    {
    }
}

/// <summary>Fake <see cref="IAmbientConnectionAccessor"/>: returns a configurable ambient connection and
/// transaction pair, or <see langword="null"/> when <see cref="Ambient"/> is left unset.</summary>
internal sealed class FakeAmbientConnectionAccessor : IAmbientConnectionAccessor
{
    public (DbConnection Connection, DbTransaction Transaction)? Ambient { get; set; }

    public Task<(DbConnection Connection, DbTransaction Transaction)?> TryGetAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Ambient);

    /// <summary>Builds a fresh, distinguishable ambient connection/transaction pair.</summary>
    public static (DbConnection Connection, DbTransaction Transaction) NewAmbient()
    {
        var connection = new StubDbConnection();
        return (connection, new FakeDbTransaction(connection));
    }
}

/// <summary>Fake <see cref="ITenantContext"/> reporting a fixed tenant (or none).</summary>
internal sealed class FakeTenantContext(TenantId? tenantId) : ITenantContext
{
    public TenantId? CurrentTenantId { get; } = tenantId;

    public string? Source => "fake";
}

/// <summary>Minimal concrete <see cref="DbException"/> for simulating a driver failure (e.g. a missing table).</summary>
internal sealed class FakeDbException(string message) : DbException(message);

/// <summary>Capturing <see cref="IAuditRecorder"/> for adapter-mapping assertions — never touches storage.</summary>
internal sealed class FakeAuditRecorder : IAuditRecorder
{
    public AuditEntry? LastEntry { get; private set; }

    public object? LastPayload { get; private set; }

    public ValueTask<Guid> RecordAsync(AuditEntry entry, object? payload = null, CancellationToken cancellationToken = default)
    {
        LastEntry = entry;
        LastPayload = payload;
        return ValueTask.FromResult(entry.EventUid);
    }
}
