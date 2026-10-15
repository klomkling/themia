using System.Data;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Themia.Data.Migrations;
using Themia.ETax.Data;
using Themia.ETax.Withholding;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Sequences;
using Themia.Framework.Data.Sequences.Migrations;
using Xunit;

namespace Themia.ETax.Withholding.Data.IntegrationTests;

[Collection(PostgresCollection.Name)]
public sealed class WithholdingNumberSourceTests(PostgresFixture postgres)
{
    private async Task<(ServiceProvider Provider, string ConnectionString)> BuildAsync(Action<WithholdingNumberOptions>? configure = null, string? existing = null)
    {
        var connectionString = existing ?? await postgres.CreateDatabaseAsync();
        ThemiaMigrations.Run(MigrationEngine.Postgres, connectionString, typeof(SequencesSchemaMigration).Assembly);
        var services = new ServiceCollection();
        services.AddSingleton<ITenantContext>(new TenantContext(new TenantId("acme")));
        services.AddThemiaSequences(o => { o.ConnectionString = connectionString; o.Engine = SequenceEngine.Postgres; });
        services.AddThemiaETaxSequenceNumbers();
        services.AddThemiaETaxWithholdingSequenceNumbers(configure);
        return (services.BuildServiceProvider(), connectionString);
    }

    private static async Task<string> NextAsync(
        IServiceProvider provider, string connectionString, bool commit, string payer = "payer-1", DateOnly? date = null)
    {
        await using var scope = provider.CreateAsyncScope();
        var source = scope.ServiceProvider.GetRequiredService<IWithholdingNumberSource>();
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        var number = await source.NextNumberAsync(tx, payer, date ?? new DateOnly(2026, 10, 14));
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

        Assert.Equal("WHT-2569-000001", await NextAsync(provider, cs, commit: true));
        Assert.Equal("WHT-2569-000002", await NextAsync(provider, cs, commit: false));      // rolled back: returned
        Assert.Equal("WHT-2569-000002", await NextAsync(provider, cs, commit: true));
        Assert.Equal("WHT-2569-000003", await NextAsync(provider, cs, commit: true));
    }

    [Fact]
    public async Task Numbers_ShouldRunSeparatelyPerPayerAndYear_AndNotShareARunWithTaxDocuments()
    {
        var (provider, cs) = await BuildAsync();
        await using var _ = provider;

        Assert.Equal("WHT-2569-000001", await NextAsync(provider, cs, true, "payer-1"));
        Assert.Equal("WHT-2569-000001", await NextAsync(provider, cs, true, "payer-2"));
        Assert.Equal("WHT-2570-000001", await NextAsync(provider, cs, true, "payer-1", new DateOnly(2027, 1, 1)));
        Assert.Equal("WHT-2569-000002", await NextAsync(provider, cs, true, "payer-1"));

        await using var scope = provider.CreateAsyncScope();
        await using var connection = new NpgsqlConnection(cs);
        await connection.OpenAsync();
        await using var tx = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted);
        var invoice = await scope.ServiceProvider.GetRequiredService<ITaxDocumentNumberSource>()
            .NextNumberAsync(tx, "payer-1", TaxDocumentKind.TaxInvoiceReceipt, new DateOnly(2026, 10, 14));
        await tx.RollbackAsync();
        Assert.Equal("TAX-2026-000001", invoice);
    }

    [Fact]
    public async Task Numbers_ShouldNeverRepeatOrSkip_WhenIssuedConcurrently()
    {
        var (provider, cs) = await BuildAsync();
        await using var _ = provider;

        var numbers = await Task.WhenAll(Enumerable.Range(0, 12).Select(_ => NextAsync(provider, cs, commit: true)));

        Assert.Equal(
            Enumerable.Range(1, 12).Select(n => $"WHT-2569-{n:D6}"),
            numbers.OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public async Task Numbers_ShouldContinueTheSameRun_WhenTheYearStyleChanges()
    {
        var (buddhist, cs) = await BuildAsync();
        await using (buddhist)
        {
            Assert.Equal("WHT-2569-000001", await NextAsync(buddhist, cs, true));
            Assert.Equal("WHT-2569-000002", await NextAsync(buddhist, cs, true));
        }

        var (gregorian, _) = await BuildAsync(o => o.Year = WithholdingNumberYear.Gregorian, cs);
        await using (gregorian)
        {
            Assert.Equal("WHT-2026-000003", await NextAsync(gregorian, cs, true));
        }
    }

    [Fact]
    public async Task Numbers_ShouldUseTheHostCounters_WhenScopedToTheHost()
    {
        var (provider, cs) = await BuildAsync(o => o.Scope = TaxDocumentNumberScope.Host);
        await using var _ = provider;

        Assert.Equal("WHT-2569-000001", await NextAsync(provider, cs, true));
        Assert.Equal("WHT-2569-000002", await NextAsync(provider, cs, true));
    }
}
