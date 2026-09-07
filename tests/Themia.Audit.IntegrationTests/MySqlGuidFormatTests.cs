using Dapper;

using MySqlConnector;

using Themia.Audit;
using Themia.Audit.MySql;

using Xunit;

namespace Themia.Audit.IntegrationTests;

/// <summary>
/// The enlisted write path never goes through <see cref="MySqlAuditDialect.CreateConnection"/>, so the
/// <c>GuidFormat</c> that call pins does not apply to it. Under
/// <c>AuditTransactionPolicy.RequireTransaction</c> — the default for activity events — the row is written
/// on the application's own connection, built from the application's own connection string.
/// <para>
/// An adopter setting <c>GuidFormat=Binary16</c> or <c>OldGuids=true</c> is making an ordinary choice for
/// their own schema. Before <see cref="IAuditDialect.BindEventUid"/> existed, that choice sent sixteen raw
/// bytes into <c>event_uid CHAR(36)</c>, and every later lookup by <see cref="AuditEntry.EventUid"/> —
/// including the dashboard's whole detail route — matched nothing.
/// </para>
/// </summary>
[Trait("Category", "Integration")]
[Collection(MySqlAuditStoreCollection.Name)]
public sealed class MySqlGuidFormatTests(MySqlAuditStoreFixture fixture)
{
    [Theory]
    [InlineData(MySqlGuidFormat.Binary16)]
    [InlineData(MySqlGuidFormat.LittleEndianBinary16)]
    [InlineData(MySqlGuidFormat.Char36)]
    public async Task Event_uid_round_trips_whatever_guid_format_the_caller_opened_with(MySqlGuidFormat format)
    {
        var builder = new MySqlConnectionStringBuilder(fixture.ConnectionString)
        {
            OldGuids = false,
            GuidFormat = format,
        };

        var entry = new AuditEntry
        {
            EventType = "GUID_FORMAT_PROBE",
            Category = AuditCategory.Activity,
            Outcome = AuditOutcome.Success,
            TenantId = $"guid-format-{format}",
            OccurredAt = DateTimeOffset.UtcNow,
        };

        // Deliberately NOT dialect.CreateConnection: this is the adopter's connection, which is what the
        // enlisted path actually writes on.
        await using (var callerConnection = new MySqlConnection(builder.ConnectionString))
        {
            await callerConnection.OpenAsync(CancellationToken.None);
            await fixture.Store.WriteAsync(entry, callerConnection, null, CancellationToken.None);
        }

        // Read back through the dialect's own pinned connection — the dashboard's path.
        await using var reader = fixture.Dialect.CreateConnection(fixture.ConnectionString);
        await reader.OpenAsync(CancellationToken.None);
        var found = await fixture.Store.GetAsync(entry.EventUid, reader, CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal(entry.EventUid, found!.EventUid);

        // And the column really holds the 36-character form, not sixteen raw bytes.
        //
        // CONCAT, not a bare SELECT of the column: this connection has GuidFormat=Char36 pinned, so the
        // driver maps CHAR(36) straight to a Guid and asking Dapper for a string throws
        // ("Object must implement IConvertible"). Concatenating produces a computed VARCHAR the driver
        // does not recognise as a Guid column, which is what lets us see the stored bytes as text.
        //
        // Without this assertion the test would pass even if writes and reads agreed on the WRONG
        // representation — the round-trip alone cannot tell a correct encoding from a consistently
        // incorrect one.
        var stored = await reader.ExecuteScalarAsync<string>(
            "SELECT CONCAT(event_uid, '') FROM themia_audit_events WHERE tenant_id = @Tenant",
            new { Tenant = entry.TenantId });
        Assert.Equal(entry.EventUid.ToString("D"), stored);
    }
}
