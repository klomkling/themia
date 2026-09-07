using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Themia.Audit.Redaction;
using Xunit;

namespace Themia.Audit.Tests;

public class AuditRecorderTests
{
    private static readonly AuditOptions Options = new() { ConnectionString = "fake", Engine = AuditEngine.Postgres };

    [Fact]
    public async Task Serializes_the_payload_and_redacts_it()
    {
        var store = new CapturingStore();
        var recorder = CreateRecorder(store);

        await recorder.RecordAsync(Valid(), new { userId = 7, password = "hunter2" });

        Assert.NotNull(store.Last);
        Assert.Contains("\"userId\":7", store.Last!.Data, StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", store.Last.Data!, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Rejects_a_string_payload()
    {
        // JsonSerializer.Serialize on a string produces a JSON string LITERAL, so AuditRedactor sees a
        // bare-string root with no property names to walk and would return it verbatim (see
        // AuditRedactorTests' bare-string-root case) — an adopter who pre-serializes their own payload
        // would otherwise get zero redaction with no error to signal it.
        var store = new CapturingStore();
        var recorder = CreateRecorder(store);

        var exception = await Assert.ThrowsAsync<ArgumentException>(
            () => recorder.RecordAsync(Valid(), """{"password":"hunter2"}""").AsTask());

        Assert.Equal("payload", exception.ParamName);
        Assert.Null(store.Last);
    }

    [Fact]
    public async Task Null_payload_leaves_data_null()
    {
        var store = new CapturingStore();
        var recorder = CreateRecorder(store);

        await recorder.RecordAsync(Valid());

        Assert.NotNull(store.Last);
        Assert.Null(store.Last!.Data);
    }

    [Fact]
    public async Task Validates_before_writing()
    {
        var store = new CapturingStore();
        var recorder = CreateRecorder(store);

        await Assert.ThrowsAsync<ArgumentException>(
            () => recorder.RecordAsync(Valid() with { Category = AuditCategory.Unspecified }).AsTask());

        Assert.Null(store.Last);
    }

    [Fact]
    public async Task Stamps_OccurredAt_from_the_time_provider_when_left_default()
    {
        var store = new CapturingStore();
        var fixedTime = new DateTimeOffset(2026, 9, 6, 12, 0, 0, TimeSpan.Zero);
        var recorder = CreateRecorder(store, new FakeTimeProvider(fixedTime));

        await recorder.RecordAsync(Valid());

        Assert.Equal(fixedTime, store.Last!.OccurredAt);
    }

    [Fact]
    public async Task Does_not_overwrite_an_OccurredAt_the_caller_already_set()
    {
        var store = new CapturingStore();
        var callerTime = new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var recorder = CreateRecorder(store, new FakeTimeProvider(DateTimeOffset.UtcNow));

        await recorder.RecordAsync(Valid() with { OccurredAt = callerTime });

        Assert.Equal(callerTime, store.Last!.OccurredAt);
    }

    [Fact]
    public async Task RecordOnAsync_writes_on_the_supplied_connection_without_opening_one()
    {
        var store = new CapturingStore();
        // A dialect that throws if CreateConnection is ever called proves RecordOnAsync never opens
        // its own connection (design §7 — IAuditStore, and every write path into it, never opens one).
        var recorder = new AuditRecorder(store, new AuditRedactor(new AuditRedactionOptions()), TimeProvider.System, new ThrowingAuditDialect(), Options);
        using var connection = new NoOpDbConnection();

        var eventUid = await recorder.RecordOnAsync(Valid(), null, connection, transaction: null, default);

        Assert.Same(connection, store.LastConnection);
        Assert.Equal(store.Last!.EventUid, eventUid);
    }

    private static AuditRecorder CreateRecorder(CapturingStore store, TimeProvider? timeProvider = null) =>
        new(store, new AuditRedactor(new AuditRedactionOptions()), timeProvider ?? TimeProvider.System, new NoOpAuditDialect(), Options);

    private static AuditEntry Valid() => new()
    {
        Category = AuditCategory.Activity,
        EventType = "TEST_EVENT",
        Outcome = AuditOutcome.Success,
    };

    private sealed class CapturingStore : IAuditStore
    {
        public AuditEntry? Last { get; private set; }

        public DbConnection? LastConnection { get; private set; }

        public Task<long> WriteAsync(AuditEntry entry, DbConnection connection, DbTransaction? transaction, CancellationToken cancellationToken)
        {
            Last = entry;
            LastConnection = connection;
            return Task.FromResult(1L);
        }

        public Task<PagedResult<AuditEntry>> QueryAsync(AuditQuery query, DbConnection connection, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<AuditEntry?> GetAsync(Guid eventUid, DbConnection connection, CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<int> PurgeAsync(DateTimeOffset olderThan, DbConnection connection, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private sealed class FakeTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    // A dialect used only by RecordOnAsync tests, where CreateConnection must never be called.
    private sealed class ThrowingAuditDialect : IAuditDialect
    {
        public DbConnection CreateConnection(string connectionString) =>
            throw new InvalidOperationException("RecordOnAsync must not open its own connection.");

        public string InsertSql => throw new NotSupportedException();

        public string SelectPageSql => throw new NotSupportedException();

        public string CountSql => throw new NotSupportedException();

        public string PurgeSql => throw new NotSupportedException();
    }

    // Returns a connection that never touches a real database, so RecordAsync (the interface path,
    // which does open its own connection via IAuditDialect) is exercisable in a pure unit test.
    private sealed class NoOpAuditDialect : IAuditDialect
    {
        public DbConnection CreateConnection(string connectionString) => new NoOpDbConnection();

        public string InsertSql => throw new NotSupportedException();

        public string SelectPageSql => throw new NotSupportedException();

        public string CountSql => throw new NotSupportedException();

        public string PurgeSql => throw new NotSupportedException();
    }

    private sealed class NoOpDbConnection : DbConnection
    {
        [AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;

        public override string Database => string.Empty;

        public override string DataSource => string.Empty;

        public override string ServerVersion => string.Empty;

        public override ConnectionState State => ConnectionState.Open;

        public override void ChangeDatabase(string databaseName)
        {
        }

        public override void Close()
        {
        }

        public override void Open()
        {
        }

        public override Task OpenAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();

        protected override DbCommand CreateDbCommand() => throw new NotSupportedException();
    }
}
