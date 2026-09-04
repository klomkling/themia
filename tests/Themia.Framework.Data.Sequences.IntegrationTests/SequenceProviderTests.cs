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
        var sut = ProviderFor("acme");
        await sut.EnsureSequenceAsync("DocNo:Invoice", startValue: 100);

        Assert.Equal(100, await sut.NextAsync("DocNo:Invoice"));
        Assert.Equal(101, await sut.NextAsync("DocNo:Invoice"));
    }

    [Fact]
    public async Task Ensure_IsIdempotentAndPreservesAnExistingCounter()
    {
        var sut = ProviderFor("acme");
        await sut.EnsureSequenceAsync("DocNo:Order", startValue: 500);
        Assert.Equal(500, await sut.NextAsync("DocNo:Order"));

        // A second seed with a different start must NOT reset the counter, or a redeploy would reissue
        // every number already handed out.
        await sut.EnsureSequenceAsync("DocNo:Order", startValue: 1);
        Assert.Equal(501, await sut.NextAsync("DocNo:Order"));
    }

    [Fact]
    public async Task Next_OnAnUnseededKey_Throws()
    {
        // Not "create it implicitly at 1": a typo in a sequence key would then silently become a brand-new
        // counter, and two spellings would hand out the same numbers.
        var sut = ProviderFor("acme");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => sut.NextAsync("DocNo:NeverSeeded"));
        Assert.Contains("DocNo:NeverSeeded", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Tenants_DoNotShareACounter()
    {
        await ProviderFor("acme").EnsureSequenceAsync("DocNo:Invoice", startValue: 1);
        await ProviderFor("globex").EnsureSequenceAsync("DocNo:Invoice", startValue: 1);

        Assert.Equal(1, await ProviderFor("acme").NextAsync("DocNo:Invoice"));
        Assert.Equal(2, await ProviderFor("acme").NextAsync("DocNo:Invoice"));
        Assert.Equal(1, await ProviderFor("globex").NextAsync("DocNo:Invoice"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public async Task Next_RejectsABlankKey(string? key)
        => await Assert.ThrowsAsync<ArgumentException>(() => ProviderFor("acme").NextAsync(key!));
}
