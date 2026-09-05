using Dapper;

using Npgsql;

using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Sequences;

using Xunit;

namespace Themia.Framework.Data.Sequences.IntegrationTests;

/// <summary>
/// The design's central safety property: a caller with no ambient tenant must FAIL, never fall through
/// to the host-level counter. A background job that lost its tenant scope would otherwise draw every
/// tenant's invoice numbers from one shared row, silently.
/// </summary>
[Trait("Category", "Integration")]
[Collection(PostgresSequenceCollection.Name)]
public sealed class SequenceTenantScopeTests(PostgresSequenceFixture fixture)
{

    // Every test method gets a fresh instance from xUnit, so this namespaces THIS test's keys and
    // nothing else's, even though every class in PostgresSequenceCollection shares one container and one
    // themia_sequences table. Tests that deliberately reuse one key within themselves (two tenants on the
    // same key, host versus tenant) still see a single value.
    private readonly string keyNamespace = Guid.NewGuid().ToString("N");

    private string Key(string name) => $"DocNo:{keyNamespace}:{name}";

    private ISequenceProvider ProviderFor(string? tenant) =>
        new SequenceProvider(
            new SequenceOptions { ConnectionString = fixture.ConnectionString, Engine = SequenceEngine.Postgres },
            new TenantContext(tenant is null ? null : new TenantId(tenant)));

    [Fact]
    public async Task NextAsync_WithNoAmbientTenant_ThrowsAndAllocatesNothing()
    {
        await ProviderFor(null).EnsureHostSequenceAsync(Key("Invoice"), startValue: 1);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ProviderFor(null).NextAsync(Key("Invoice")));

        // The message has to send the reader to the right layer, not just say "no tenant".
        Assert.Contains("BackgroundTenantScope", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Host", ex.Message, StringComparison.Ordinal);

        // And nothing moved: the host counter is untouched, so the refusal is not a silent allocation.
        Assert.Equal(1, await ProviderFor(null).NextHostAsync(Key("Invoice")));
    }

    [Fact]
    public async Task EnsureSequenceAsync_WithNoAmbientTenant_Throws()
        => await Assert.ThrowsAsync<InvalidOperationException>(
            () => ProviderFor(null).EnsureSequenceAsync(Key("Invoice")));

    [Fact]
    public async Task HostAndTenant_WithTheSameKey_AreDifferentCounters()
    {
        await ProviderFor(null).EnsureHostSequenceAsync(Key("Shared"), startValue: 1);
        await ProviderFor("acme").EnsureSequenceAsync(Key("Shared"), startValue: 1);

        Assert.Equal(1, await ProviderFor(null).NextHostAsync(Key("Shared")));
        Assert.Equal(2, await ProviderFor(null).NextHostAsync(Key("Shared")));
        Assert.Equal(1, await ProviderFor("acme").NextAsync(Key("Shared")));
    }

    [Fact]
    public async Task TheHostRowIsStoredAsAnEmptyTenantId()
    {
        await ProviderFor(null).EnsureHostSequenceAsync(Key("HostOnly"), startValue: 7);

        await using var conn = new NpgsqlConnection(fixture.ConnectionString);
        var tenantId = await conn.ExecuteScalarAsync<string>(
            "SELECT tenant_id FROM themia_sequences WHERE sequence_key = @key",
            new { key = Key("HostOnly") });

        // '' and not NULL: the primary key cannot hold NULL, and TenantId's constructor rejects
        // whitespace, so no real tenant can collide with this row.
        Assert.Equal(string.Empty, tenantId);
    }
}
