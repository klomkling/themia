using Npgsql;
using Xunit;

namespace Themia.ETax.Withholding.Data.IntegrationTests;

/// <summary>Raw helpers over one test database: run SQL, read a scalar, and run SQL expecting a database guard to refuse it.</summary>
internal sealed class TaxDocumentDb(string connectionString)
{
    public string ConnectionString { get; } = connectionString;

    public async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    /// <summary>Runs the statements on ONE connection, so BEGIN ... COMMIT spans them.</summary>
    public async Task RunAsync(IEnumerable<string> statements)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        foreach (var statement in statements)
        {
            await using var command = new NpgsqlCommand(statement, connection);
            await command.ExecuteNonQueryAsync();
        }
    }

    public async Task<T?> ScalarAsync<T>(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        var value = await command.ExecuteScalarAsync();
        return value is null or DBNull ? default : (T)value;
    }

    /// <summary>Runs <paramref name="sql"/> expecting the guard to refuse it; returns the PostgreSQL error.</summary>
    public async Task<PostgresException> RefusedAsync(string sql) =>
        await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(sql));
}
