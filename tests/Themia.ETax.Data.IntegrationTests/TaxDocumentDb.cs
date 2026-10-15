using Npgsql;
using Xunit;

namespace Themia.ETax.Data.IntegrationTests;

/// <summary>Raw helpers over one test database: a plain tax document table in the shape an app might have, and ways to run SQL against it.</summary>
internal sealed class TaxDocumentDb(string connectionString)
{
    public const int Issued = 1;
    public const int Sent = 3;
    public const int Failed = 4;
    public const int Cancelled = 5;

    public string ConnectionString { get; } = connectionString;

    public static TaxDocumentTableMap Map(string table = "tax_documents", string? schema = null) =>
        new() { Table = table, Schema = schema, SentStateValue = Sent, CancelledStateValue = Cancelled };

    public async Task ExecuteAsync(string sql)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

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

    public Task CreateTableAsync(string qualifiedTable = "tax_documents") => ExecuteAsync($"""
        CREATE TABLE {qualifiedTable} (
            id uuid PRIMARY KEY,
            kind int NOT NULL,
            source_event_id text NOT NULL,
            revision int NOT NULL,
            seller_id text NOT NULL,
            document_number text NULL,
            issue_date date NULL,
            snapshot_json text NULL,
            snapshot_sha256 text NULL,
            snapshot_schema_version int NULL,
            template_version text NULL,
            state int NOT NULL,
            buyer_email text NULL,
            attempts int NOT NULL DEFAULT 0,
            replaced_by_document_id uuid NULL)
        """);

    /// <summary>A document issued by Themia: it has a snapshot.</summary>
    public Task<Guid> InsertIssuedAsync(string seller = "seller-1", int state = Issued, string number = "TAX-1", string table = "tax_documents") =>
        InsertAsync(table, seller, state, number, snapshot: "{\"a\":1}");

    /// <summary>A document that existed before Themia.ETax: no snapshot.</summary>
    public Task<Guid> InsertLegacyAsync(string seller = "seller-1", int state = Issued, string number = "OLD-1", string table = "tax_documents") =>
        InsertAsync(table, seller, state, number, snapshot: null);

    private async Task<Guid> InsertAsync(string table, string seller, int state, string number, string? snapshot)
    {
        var id = Guid.NewGuid();
        await ExecuteAsync($"""
            INSERT INTO {table} (id, kind, source_event_id, revision, seller_id, document_number, issue_date,
                                 snapshot_json, snapshot_sha256, snapshot_schema_version, template_version, state)
            VALUES ('{id}', 1, 'src-{id:N}', 1, '{seller}', '{number}', DATE '2026-10-14',
                    {(snapshot is null ? "NULL" : $"'{snapshot}'")}, {(snapshot is null ? "NULL" : "'abc'")}, {(snapshot is null ? "NULL" : "1")},
                    {(snapshot is null ? "NULL" : "'1'")}, {state})
            """);
        return id;
    }

    /// <summary>Runs <paramref name="sql"/> expecting the guard to refuse it; returns the PostgreSQL error.</summary>
    public async Task<PostgresException> RefusedAsync(string sql)
    {
        var ex = await Assert.ThrowsAsync<PostgresException>(() => ExecuteAsync(sql));
        return ex;
    }
}
