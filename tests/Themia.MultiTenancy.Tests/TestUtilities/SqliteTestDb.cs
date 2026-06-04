using System.Data;
using Microsoft.Data.Sqlite;
using Dapper;

namespace Themia.MultiTenancy.Tests.TestUtilities;

/// <summary>
/// Utility to create and manage in-memory SQLite databases for DapperTenantStore tests.
/// </summary>
public sealed class SqliteTestDb : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly string _tableName;
    private bool _disposed;

    public string ConnectionString => _connection.ConnectionString;

    private SqliteTestDb(SqliteConnection connection, string tableName)
    {
        _connection = connection;
        _tableName = tableName;
    }

    public static async Task<SqliteTestDb> CreateAsync(string tableName = "tenants")
    {
        var connection = new SqliteConnection("DataSource=:memory:");
        await connection.OpenAsync();

        // Create the tenants table
        var createTableSql = $@"
            CREATE TABLE {tableName} (
                id TEXT PRIMARY KEY,
                identifier TEXT NOT NULL UNIQUE,
                name TEXT,
                environment TEXT,
                connection_string TEXT
            );";

        await connection.ExecuteAsync(createTableSql);

        return new SqliteTestDb(connection, tableName);
    }

    public async Task SeedTenantsAsync(params (string Id, string Identifier, string? Name, string? Environment, string? ConnectionString)[] tenants)
    {
        foreach (var (id, identifier, name, environment, connectionString) in tenants)
        {
            await _connection.ExecuteAsync(
                $"INSERT INTO {_tableName} (id, identifier, name, environment, connection_string) VALUES (@Id, @Identifier, @Name, @Environment, @ConnectionString)",
                new { Id = id, Identifier = identifier, Name = name, Environment = environment, ConnectionString = connectionString });
        }
    }

    public Task<IDbConnection> GetConnectionAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Return a wrapper that prevents disposal since we manage the connection lifecycle
        return Task.FromResult<IDbConnection>(new NonDisposableConnectionWrapper(_connection));
    }

    public Func<CancellationToken, Task<IDbConnection>> GetConnectionFactory()
    {
        return GetConnectionAsync;
    }

    public void Dispose()
    {
        if (_disposed) return;

        _connection?.Dispose();
        _disposed = true;
    }

    /// <summary>
    /// Wrapper that prevents the underlying connection from being disposed by DapperTenantStore's using statement.
    /// We manage the connection lifecycle ourselves.
    /// </summary>
    private sealed class NonDisposableConnectionWrapper : IDbConnection
    {
        private readonly IDbConnection _inner;

        public NonDisposableConnectionWrapper(IDbConnection inner) => _inner = inner;

        [System.Diagnostics.CodeAnalysis.AllowNull]
        public string ConnectionString { get => _inner.ConnectionString; set => _inner.ConnectionString = value; }
        public int ConnectionTimeout => _inner.ConnectionTimeout;
        public string Database => _inner.Database;
        public ConnectionState State => _inner.State;

        public IDbTransaction BeginTransaction() => _inner.BeginTransaction();
        public IDbTransaction BeginTransaction(IsolationLevel il) => _inner.BeginTransaction(il);
        public void ChangeDatabase(string databaseName) => _inner.ChangeDatabase(databaseName);
        public void Close() { } // No-op: don't close the connection
        public IDbCommand CreateCommand() => _inner.CreateCommand();
        public void Dispose() { } // No-op: prevent disposal
        public void Open() => _inner.Open();
    }
}
