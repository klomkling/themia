using System.Data;
using System.Data.Common;
using Themia.Framework.Data.Sequences;
using Xunit;

namespace Themia.ETax.Data.Tests;

public sealed class SequenceTaxDocumentNumberSourceTests
{
    private sealed class FakeAllocator : ITransactionalSequenceAllocator
    {
        public List<(string Key, bool Host)> Calls { get; } = [];

        public long Next { get; set; } = 1;

        public Task<long> NextInTransactionAsync(DbTransaction tx, string sequenceKey, CancellationToken ct = default)
        {
            Calls.Add((sequenceKey, false));
            return Task.FromResult(Next++);
        }

        public Task<long> NextHostInTransactionAsync(DbTransaction tx, string sequenceKey, CancellationToken ct = default)
        {
            Calls.Add((sequenceKey, true));
            return Task.FromResult(Next++);
        }
    }

    private sealed class NoTx : DbTransaction
    {
        protected override DbConnection? DbConnection => null;

        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;

        public override void Commit() { }

        public override void Rollback() { }
    }

    private static readonly DateOnly Date = new(2026, 10, 14);
    private readonly FakeAllocator _allocator = new();

    private SequenceTaxDocumentNumberSource Sut(Action<TaxDocumentNumberOptions>? configure = null)
    {
        var options = new TaxDocumentNumberOptions();
        configure?.Invoke(options);
        return new SequenceTaxDocumentNumberSource(_allocator, options);
    }

    [Fact]
    public async Task NextNumberAsync_ShouldFormatPrefixYearAndPaddedCounter()
    {
        _allocator.Next = 42;

        var number = await Sut().NextNumberAsync(new NoTx(), "seller-1", TaxDocumentKind.TaxInvoiceReceipt, Date);

        Assert.Equal("TAX-2026-000042", number);
        Assert.Equal(("DocNo:ETax:TaxInvoiceReceipt:seller-1:2026", false), Assert.Single(_allocator.Calls));
    }

    [Theory]
    [InlineData(TaxDocumentKind.CreditNote, "CN-2026-000001")]
    [InlineData(TaxDocumentKind.DebitNote, "DN-2026-000001")]
    public async Task NextNumberAsync_ShouldUseTheKindsPrefixAndItsOwnRun(TaxDocumentKind kind, string expected)
    {
        Assert.Equal(expected, await Sut().NextNumberAsync(new NoTx(), "s", kind, Date));
        Assert.Contains(kind.ToString(), _allocator.Calls[0].Key);
    }

    [Theory]
    [InlineData(TaxDocumentNumberPeriod.Never, "TAX-000001", "DocNo:ETax:TaxInvoiceReceipt:s")]
    [InlineData(TaxDocumentNumberPeriod.Yearly, "TAX-2026-000001", "DocNo:ETax:TaxInvoiceReceipt:s:2026")]
    [InlineData(TaxDocumentNumberPeriod.Monthly, "TAX-202610-000001", "DocNo:ETax:TaxInvoiceReceipt:s:202610")]
    public async Task NextNumberAsync_ShouldRestartTheRunPerPeriod(TaxDocumentNumberPeriod period, string expected, string key)
    {
        var number = await Sut(o => o.Period = period).NextNumberAsync(new NoTx(), "s", TaxDocumentKind.TaxInvoiceReceipt, Date);

        Assert.Equal(expected, number);
        Assert.Equal(key, _allocator.Calls[0].Key);
    }

    [Fact]
    public async Task NextNumberAsync_ShouldUseTheHostCounters_WhenScopedToTheHost()
    {
        await Sut(o => o.Scope = TaxDocumentNumberScope.Host).NextNumberAsync(new NoTx(), "s", TaxDocumentKind.CreditNote, Date);

        Assert.True(Assert.Single(_allocator.Calls).Host);
    }

    [Fact]
    public async Task NextNumberAsync_ShouldHonourCustomPrefixAndPadding()
    {
        var number = await Sut(o => { o.Prefixes[TaxDocumentKind.TaxInvoiceReceipt] = "INV"; o.Padding = 4; })
            .NextNumberAsync(new NoTx(), "s", TaxDocumentKind.TaxInvoiceReceipt, Date);

        Assert.Equal("INV-2026-0001", number);
    }

    [Fact]
    public async Task NextNumberAsync_ShouldRefuseASellerIdThatOverflowsTheKeyLimit_WithoutAllocating()
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(() =>
            Sut().NextNumberAsync(new NoTx(), new string('x', 90), TaxDocumentKind.TaxInvoiceReceipt, Date));

        Assert.Equal("sellerId", ex.ParamName);
        Assert.Empty(_allocator.Calls);
    }

    [Fact]
    public void Options_ShouldRejectBadPaddingAndMissingPrefixes()
    {
        Assert.Throws<InvalidOperationException>(() => Sut(o => o.Padding = 0));
        Assert.Throws<InvalidOperationException>(() => Sut(o => o.Prefixes[TaxDocumentKind.DebitNote] = " "));
        Assert.Throws<InvalidOperationException>(() => Sut(o => o.Prefixes.Remove(TaxDocumentKind.CreditNote)));
    }
}
