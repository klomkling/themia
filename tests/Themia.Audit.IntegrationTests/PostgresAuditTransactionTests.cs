using Dapper;
using Npgsql;
using Themia.Audit;
using Themia.Audit.Redaction;
using Xunit;

namespace Themia.Audit.IntegrationTests;

[Trait("Category", "Integration")]
[Collection(PostgresAuditStoreCollection.Name)]
public sealed class PostgresAuditTransactionTests(PostgresAuditStoreFixture fixture)
{
    private readonly AuditRecorder recorder = new(
        fixture.Store,
        new AuditRedactor(new AuditRedactionOptions()),
        TimeProvider.System,
        fixture.Dialect,
        new AuditOptions { ConnectionString = fixture.ConnectionString, Engine = AuditEngine.Postgres });

    [Fact]
    public async Task Audit_row_is_absent_after_caller_transaction_rolls_back()
    {
        var tenantId = $"t-{Guid.NewGuid():N}";
        var entry = new AuditEntry
        {
            EventType = "TEST_ROLLBACK",
            Category = AuditCategory.Activity,
            Outcome = AuditOutcome.Success,
            TenantId = tenantId,
        };

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using (var tx = await connection.BeginTransactionAsync())
        {
            await recorder.RecordOnAsync(entry, new { amount = 100 }, connection, tx);
            await tx.RollbackAsync();
        }

        var count = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM themia_audit_events WHERE event_uid = @EventUid",
            new { entry.EventUid });

        Assert.Equal(0, count);
    }

    [Fact]
    public async Task Audit_row_is_present_after_caller_transaction_commits_and_jsonb_queriable()
    {
        var tenantId = $"t-{Guid.NewGuid():N}";
        var entry = new AuditEntry
        {
            EventType = "TEST_COMMIT",
            Category = AuditCategory.Activity,
            Outcome = AuditOutcome.Success,
            TenantId = tenantId,
        };

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();
        await using (var tx = await connection.BeginTransactionAsync())
        {
            await recorder.RecordOnAsync(entry, new { orderId = "ORD-123", status = "paid" }, connection, tx);
            await tx.CommitAsync();
        }

        // Verify row exists and adopter cast data::jsonb queries the data column
        var orderId = await connection.ExecuteScalarAsync<string>(
            "SELECT (data::jsonb)->>'orderId' FROM themia_audit_events WHERE event_uid = @EventUid",
            new { entry.EventUid });

        Assert.Equal("ORD-123", orderId);
    }

    [Fact]
    public async Task Null_transaction_records_unenlisted_and_persists_immediately()
    {
        var tenantId = $"t-{Guid.NewGuid():N}";
        var entry = new AuditEntry
        {
            EventType = "TEST_UNENLISTED",
            Category = AuditCategory.Activity,
            Outcome = AuditOutcome.Success,
            TenantId = tenantId,
        };

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        await recorder.RecordOnAsync(entry, new { unenlisted = true }, connection, transaction: null);

        var count = await connection.ExecuteScalarAsync<int>(
            "SELECT COUNT(*) FROM themia_audit_events WHERE event_uid = @EventUid",
            new { entry.EventUid });

        Assert.Equal(1, count);
    }

    [Fact]
    public async Task Thai_text_payload_round_trips_byte_identical_without_unicode_escaping()
    {
        var tenantId = $"t-{Guid.NewGuid():N}";
        var entry = new AuditEntry
        {
            EventType = "TEST_THAI",
            Category = AuditCategory.Activity,
            Outcome = AuditOutcome.Success,
            TenantId = tenantId,
        };

        const string rawThaiJson = """{"title":"ใบเสร็จรับเงิน/ใบกำกับภาษี","customer":"สมชาย ใจดี"}""";

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        using var payloadDoc = AuditPayload.FromRawJson(rawThaiJson);
        await recorder.RecordOnAsync(entry, payloadDoc, connection, transaction: null);

        var storedData = await connection.ExecuteScalarAsync<string>(
            "SELECT data FROM themia_audit_events WHERE event_uid = @EventUid",
            new { entry.EventUid });

        var readEntry = await fixture.Store.GetAsync(entry.EventUid, connection, default);

        Assert.NotNull(storedData);
        Assert.NotNull(readEntry);

        // Byte-identical raw JSON text:
        Assert.Equal(rawThaiJson, storedData);
        Assert.Equal(rawThaiJson, readEntry.Data);

        // Verify no unicode escaping (\u0E...) occurred
        Assert.DoesNotContain(@"\u", readEntry.Data, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Html_sensitive_characters_are_escaped_to_prevent_xss()
    {
        var tenantId = $"t-{Guid.NewGuid():N}";
        var entry = new AuditEntry
        {
            EventType = "TEST_XSS_ESCAPING",
            Category = AuditCategory.Activity,
            Outcome = AuditOutcome.Success,
            TenantId = tenantId,
        };

        await using var connection = new NpgsqlConnection(fixture.ConnectionString);
        await connection.OpenAsync();

        var payload = new { input = "<script>alert('xss') & test</script>" };
        await recorder.RecordOnAsync(entry, payload, connection, transaction: null);

        var readEntry = await fixture.Store.GetAsync(entry.EventUid, connection, default);

        Assert.NotNull(readEntry);
        Assert.NotNull(readEntry.Data);

        // Verify HTML sensitive characters are escaped (\u003C, \u003E, \u0026, \u0027)
        Assert.DoesNotContain("<script>", readEntry.Data, StringComparison.Ordinal);
        Assert.Contains(@"\u003Cscript\u003E", readEntry.Data, StringComparison.Ordinal);
        Assert.Contains(@"\u0026", readEntry.Data, StringComparison.Ordinal);
    }
}
