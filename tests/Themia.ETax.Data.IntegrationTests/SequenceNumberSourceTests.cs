using System.Data;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Themia.Data.Migrations;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Sequences;
using Themia.Framework.Data.Sequences.Migrations;
using Xunit;

namespace Themia.ETax.Data.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class SequenceNumberSourceTests(PostgresFixture postgres)
{
    private async Task<(ServiceProvider Provider, string ConnectionString)> BuildAsync(Action<TaxDocumentNumberOptions>? configure = null)
    {
        var connectionString = await postgres.CreateDatabaseAsync();
        ThemiaMigrations.Run(MigrationEngine.Postgres, connectionString, typeof(SequencesSchemaMigration).Assembly);
        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(new TenantContext(new TenantId("acme")));
        services.AddThemiaSequences(o => { o.ConnectionString = connectionString; o.Engine = SequenceEngine.Postgres; });
        services.AddThemiaETaxSequenceNumbers(configure);
        return (services.BuildServiceProvider(), connectionString);
    }

    private static async Task<string> NextAsync(
        IServiceProvider provider, string connectionString, bool commit, string seller = "seller-1",
        TaxDocumentKind kind = TaxDocumentKind.TaxInvoiceReceipt, DateOnly? date = null)
    {
        await using var scope = provider.CreateAsyncScope();
        var source = scope.ServiceProvider.GetRequiredService<ITaxDocumentNumberSource>();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        var number = await source.NextNumberAsync(tx, seller, kind, date ?? new DateOnly(2026, 10, 14));
        if (commit)
            await tx.CommitAsync();
        else
            await tx.RollbackAsync();
        return number;
    }

    [Fact]
    public async Task Numbers_ShouldBeGaplessAcrossARollback()
    {
        var (provider, cs) = await BuildAsync();
        await using var _ = provider;

        Assert.Equal("TAX-2026-000001", await NextAsync(provider, cs, commit: true));
        Assert.Equal("TAX-2026-000002", await NextAsync(provider, cs, commit: false));      // rolled back: returned
        Assert.Equal("TAX-2026-000002", await NextAsync(provider, cs, commit: true));
        Assert.Equal("TAX-2026-000003", await NextAsync(provider, cs, commit: true));
    }

    [Fact]
    public async Task Numbers_ShouldRunSeparatelyPerSellerKindAndYear()
    {
        var (provider, cs) = await BuildAsync();
        await using var _ = provider;

        Assert.Equal("TAX-2026-000001", await NextAsync(provider, cs, true, "seller-1"));
        Assert.Equal("TAX-2026-000001", await NextAsync(provider, cs, true, "seller-2"));
        Assert.Equal("CN-2026-000001", await NextAsync(provider, cs, true, "seller-1", TaxDocumentKind.CreditNote));
        Assert.Equal("TAX-2027-000001", await NextAsync(provider, cs, true, "seller-1", date: new DateOnly(2027, 1, 1)));
        Assert.Equal("TAX-2026-000002", await NextAsync(provider, cs, true, "seller-1"));
    }

    [Fact]
    public async Task Numbers_ShouldNeverRepeatOrSkip_WhenIssuedConcurrently()
    {
        var (provider, cs) = await BuildAsync();
        await using var _ = provider;

        var numbers = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => NextAsync(provider, cs, commit: true)));

        Assert.Equal(
            Enumerable.Range(1, 12).Select(n => $"TAX-2026-{n:D6}"),
            numbers.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Numbers_ShouldUseTheHostCounters_WhenScopedToTheHost()
    {
        var (provider, cs) = await BuildAsync(o => o.Scope = TaxDocumentNumberScope.Host);
        await using var _ = provider;

        Assert.Equal("TAX-2026-000001", await NextAsync(provider, cs, true));
        Assert.Equal("TAX-2026-000002", await NextAsync(provider, cs, true));
    }
}
