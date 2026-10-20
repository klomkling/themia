using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using System.Net.Sockets;
using FluentMigrator.Runner;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Themia.Data.Migrations.Tests;

/// <summary>
/// The lock connection is opened before any migration runs, so a dropped open or a dropped lock session
/// has applied nothing and is safe to retry. Anything past that point (a genuine lock wait timeout, a
/// failing migration) must never be retried.
/// </summary>
public class MigrationLockRetryTests
{
    private const string Secret = "Password=hunter2";

    private static readonly Exception[] OneDroppedOpen = [new IOException(Secret)];

    [Fact]
    public void RunExclusive_RetriesTheOpen_WhenItIsDroppedTransiently()
    {
        var adapter = new ScriptedAdapter(openFailures: [new IOException(Secret), new SocketException()]);
        var options = Options(adapter);
        var migrated = 0;

        MigrationLock.RunExclusive(adapter, "cs", options, () => migrated++);

        Assert.Equal(1, migrated);
        Assert.Equal(3, adapter.ConnectionsCreated);
        Assert.Equal([TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2)], adapter.Sleeps);
    }

    [Fact]
    public void RunExclusive_ThrowsTheLastFailure_WhenEveryAttemptIsDropped()
    {
        var adapter = new ScriptedAdapter(openFailures: [new IOException("a"), new IOException("b"), new IOException("c"), new IOException("d")]);
        var migrated = 0;

        var ex = Assert.Throws<MigrationLockException>(
            () => MigrationLock.RunExclusive(adapter, "cs", Options(adapter), () => migrated++));

        Assert.Equal(0, migrated);
        Assert.Equal(4, adapter.ConnectionsCreated);
        Assert.Equal("d", ex.InnerException!.Message);
        Assert.Contains("could not open the connection", ex.Message);
    }

    [Fact]
    public void RunExclusive_DoesNotRetry_WhenTheOpenFailureIsNotTransient()
    {
        var adapter = new ScriptedAdapter(openFailures: [new InvalidOperationException("bad credentials")]);

        Assert.Throws<MigrationLockException>(
            () => MigrationLock.RunExclusive(adapter, "cs", Options(adapter), () => { }));

        Assert.Equal(1, adapter.ConnectionsCreated);
        Assert.Empty(adapter.Sleeps);
    }

    [Fact]
    public void RunExclusive_RetriesWhatTheAdapterCallsTransient()
    {
        var adapter = new ScriptedAdapter(openFailures: [new FlaggedDbException()]) { Transient = _ => true };

        MigrationLock.RunExclusive(adapter, "cs", Options(adapter), () => { });

        Assert.Equal(2, adapter.ConnectionsCreated);
    }

    [Fact]
    public void RunExclusive_IgnoresTheDriversOwnIsTransientFlag()
    {
        var adapter = new ScriptedAdapter(openFailures: [new FlaggedDbException()]);

        Assert.Throws<MigrationLockException>(
            () => MigrationLock.RunExclusive(adapter, "cs", Options(adapter), () => { }));

        Assert.Equal(1, adapter.ConnectionsCreated);
    }

    [Fact]
    public void RunExclusive_RetriesAConnectTimeout()
    {
        var adapter = new ScriptedAdapter(openFailures: [new InvalidOperationException("connect", new TimeoutException())]);

        MigrationLock.RunExclusive(adapter, "cs", Options(adapter), () => { });

        Assert.Equal(2, adapter.ConnectionsCreated);
    }

    [Fact]
    public void RunExclusive_DoesNotRetry_ACommandTimeoutWhileTakingTheLock()
    {
        var adapter = new ScriptedAdapter(
            acquireFailures: [new InvalidOperationException("command", new TimeoutException())]) { Transient = _ => true };

        Assert.Throws<MigrationLockException>(
            () => MigrationLock.RunExclusive(adapter, "cs", Options(adapter), () => { }));

        Assert.Equal(1, adapter.ConnectionsCreated);
    }

    [Fact]
    public void RunExclusive_RetriesOnAFreshConnection_WhenTheLockSessionDropsBeforeTheLockIsHeld()
    {
        var adapter = new ScriptedAdapter(acquireFailures: [new IOException(Secret)]);
        var migrated = 0;

        MigrationLock.RunExclusive(adapter, "cs", Options(adapter), () => migrated++);

        Assert.Equal(1, migrated);
        Assert.Equal(2, adapter.ConnectionsCreated);
        Assert.Equal(2, adapter.DisposedConnections);
    }

    [Fact]
    public void RunExclusive_DoesNotRetry_AGenuineLockWaitTimeout()
    {
        var adapter = new ScriptedAdapter { TimeoutCause = new FlaggedDbException(), Transient = _ => true };

        var ex = Assert.Throws<MigrationLockException>(
            () => MigrationLock.RunExclusive(adapter, "cs", Options(adapter), () => { }));

        Assert.Contains("timed out", ex.Message);
        Assert.Equal(1, adapter.ConnectionsCreated);
    }

    [Fact]
    public void RunExclusive_DoesNotRetry_ATransientFailureInsideTheMigrationBody()
    {
        var adapter = new ScriptedAdapter();
        var migrated = 0;

        Assert.Throws<IOException>(() => MigrationLock.RunExclusive(adapter, "cs", Options(adapter), () =>
        {
            migrated++;
            throw new IOException("dropped mid-migration");
        }));

        Assert.Equal(1, migrated);
        Assert.Equal(1, adapter.ConnectionsCreated);
    }

    [Fact]
    public void RunExclusive_DoesNotRetry_WhenRetriesAreSetToZero()
    {
        var adapter = new ScriptedAdapter(openFailures: OneDroppedOpen);
        var options = Options(adapter, retries: 0);

        Assert.Throws<MigrationLockException>(() => MigrationLock.RunExclusive(adapter, "cs", options, () => { }));

        Assert.Equal(1, adapter.ConnectionsCreated);
    }

    [Fact]
    public void RunExclusive_LogsEachRetryWithTheExceptionTypeOnly()
    {
        var adapter = new ScriptedAdapter(openFailures: OneDroppedOpen);
        var logger = new CollectingLogger();
        var options = Options(adapter, logger);

        MigrationLock.RunExclusive(adapter, "cs", options, () => { });

        var warning = Assert.Single(logger.Entries, e => e.Level == LogLevel.Warning);
        Assert.Contains(nameof(IOException), warning.Text);
        Assert.DoesNotContain(Secret, warning.Text);
        Assert.Null(warning.Exception);
    }

    [Fact]
    public void Options_DefaultToThreeRetriesWithAOneSecondBackoff()
    {
        var options = new ThemiaMigrationOptions();

        Assert.Equal(3, options.LockConnectRetries);
        Assert.Equal(TimeSpan.FromSeconds(1), options.LockConnectBackoff);
    }

    private static ThemiaMigrationOptions Options(ScriptedAdapter adapter, ILogger? logger = null, int retries = 3) =>
        new()
        {
            Logger = logger,
            LockConnectRetries = retries,
            Sleep = adapter.Sleeps.Add,
        };

    private sealed class ScriptedAdapter(
        IReadOnlyList<Exception>? openFailures = null,
        IReadOnlyList<Exception>? acquireFailures = null) : IMigrationEngineAdapter
    {
        private readonly Queue<Exception> _openFailures = new(openFailures ?? []);
        private readonly Queue<Exception> _acquireFailures = new(acquireFailures ?? []);

        public int ConnectionsCreated { get; private set; }

        public int DisposedConnections { get; set; }

        public List<TimeSpan> Sleeps { get; } = [];

        public Exception? TimeoutCause { get; init; }

        public Func<Exception, bool> Transient { get; init; } = _ => false;

        public bool IsTransientFailure(Exception failure) => Transient(failure);

        public MigrationEngine Engine => MigrationEngine.Postgres;

        public string DisplayName => "Scripted";

        public void ConfigureRunner(IMigrationRunnerBuilder builder) => throw new NotSupportedException();

        public DbConnection CreateConnection(string connectionString) => throw new NotSupportedException();

        public DbConnection CreateUnpooledConnection(string connectionString)
        {
            ConnectionsCreated++;
            return new FakeConnection(this, _openFailures.TryDequeue(out var failure) ? failure : null);
        }

        public bool TryAcquireLock(DbConnection connection, string scope, TimeSpan timeout, out Exception? timeoutCause)
        {
            timeoutCause = TimeoutCause;
            if (_acquireFailures.TryDequeue(out var failure))
                throw failure;

            return TimeoutCause is null;
        }

        public void ReleaseLock(DbConnection connection, string scope)
        {
        }
    }

    private sealed class FakeConnection(ScriptedAdapter owner, Exception? openFailure) : DbConnection
    {
        [AllowNull]
        public override string ConnectionString { get; set; } = string.Empty;

        public override string Database => "app";

        public override string DataSource => "fake";

        public override string ServerVersion => "0";

        public override ConnectionState State => ConnectionState.Open;

        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

        public override void Close()
        {
        }

        public override void Open()
        {
            if (openFailure is not null)
                throw openFailure;
        }

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();

        protected override DbCommand CreateDbCommand() => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                owner.DisposedConnections++;

            base.Dispose(disposing);
        }
    }

    private sealed class FlaggedDbException() : DbException("flagged")
    {
        public override bool IsTransient => true;
    }

    private sealed class CollectingLogger : ILogger
    {
        public List<(LogLevel Level, string Text, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception), exception));
    }
}
