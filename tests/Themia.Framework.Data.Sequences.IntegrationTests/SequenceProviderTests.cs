using Testcontainers.PostgreSql;

using Themia.Data.Migrations;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Sequences;
using Themia.Framework.Data.Sequences.Migrations;

using Xunit;

namespace Themia.Framework.Data.Sequences.IntegrationTests;

[Trait("Category", "Integration")]
public sealed class SequenceProviderTests : IAsyncLifetime
{

    // Every test method gets a fresh instance from xUnit, so this namespaces THIS test's keys and
    // nothing else's. Tests that deliberately reuse one key within themselves (two tenants on the same
    // key, host versus tenant) still see a single value. Without it the suite depends on a fresh
    // container per test, which is the cost the repo's shared-fixture pattern exists to avoid.
    private readonly string keyNamespace = Guid.NewGuid().ToString("N");

    private string Key(string name) => $"DocNo:{keyNamespace}:{name}";
    private readonly PostgreSqlContainer container = new PostgreSqlBuilder("postgres:16-alpine").Build();

    public async Task InitializeAsync()
    {
        await container.StartAsync();
        ThemiaMigrations.Run(MigrationEngine.Postgres, container.GetConnectionString(),
            typeof(SequencesSchemaMigration).Assembly);
    }

    public async Task DisposeAsync() => await container.DisposeAsync();

    private ISequenceProvider ProviderFor(string? tenant) =>
        new SequenceProvider(
            new SequenceOptions { ConnectionString = container.GetConnectionString(), Engine = SequenceEngine.Postgres },
            new TenantContext(tenant is null ? null : new TenantId(tenant)));

    [Fact]
    public async Task Next_ReturnsTheSeededStartValueThenAdvances()
    {
        // Guid-suffixed key: this suite currently gets a fresh container per test method, but the key must
        // not depend on that isolation — a later switch to a shared per-engine fixture (as in
        // ChallengeEngineFixtures) must not turn a passing test red just because it shares a key with
        // another test.
        var key = Key("Invoice");
        var sut = ProviderFor("acme");
        await sut.EnsureSequenceAsync(key, startValue: 100);

        Assert.Equal(100, await sut.NextAsync(key));
        Assert.Equal(101, await sut.NextAsync(key));
    }

    [Fact]
    public async Task Ensure_IsIdempotentAndPreservesAnExistingCounter()
    {
        var sut = ProviderFor("acme");
        await sut.EnsureSequenceAsync(Key("Order"), startValue: 500);
        Assert.Equal(500, await sut.NextAsync(Key("Order")));

        // A second seed with a different start must NOT reset the counter, or a redeploy would reissue
        // every number already handed out.
        await sut.EnsureSequenceAsync(Key("Order"), startValue: 1);
        Assert.Equal(501, await sut.NextAsync(Key("Order")));
    }

    [Fact]
    public async Task Next_OnAnUnseededKey_Throws()
    {
        // Not "create it implicitly at 1": a typo in a sequence key would then silently become a brand-new
        // counter, and two spellings would hand out the same numbers.
        var sut = ProviderFor("acme");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.NextAsync(Key("NeverSeeded")));
        Assert.Contains(Key("NeverSeeded"), ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tenants_DoNotShareACounter()
    {
        await ProviderFor("acme").EnsureSequenceAsync(Key("Invoice"), startValue: 1);
        await ProviderFor("globex").EnsureSequenceAsync(Key("Invoice"), startValue: 1);

        Assert.Equal(1, await ProviderFor("acme").NextAsync(Key("Invoice")));
        Assert.Equal(2, await ProviderFor("acme").NextAsync(Key("Invoice")));
        Assert.Equal(1, await ProviderFor("globex").NextAsync(Key("Invoice")));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public async Task Next_RejectsABlankKey(string? key)
        // ThrowsAnyAsync, not ThrowsAsync: ArgumentException.ThrowIfNullOrEmpty raises the
        // ArgumentNullException SUBTYPE for null, which an exact-type assertion rejects. The subtype is
        // the more informative outcome and the interface's documented ArgumentException contract already
        // covers it, so the assertion widens rather than the guard narrowing. Same shape as
        // tests/Themia.Modules.Pdf.Tests/PdfDocumentRendererTests.cs.
        => await Assert.ThrowsAnyAsync<ArgumentException>(() => ProviderFor("acme").NextAsync(key!));
}
