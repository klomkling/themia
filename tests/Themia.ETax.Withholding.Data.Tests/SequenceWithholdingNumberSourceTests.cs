using System.Data;
using System.Data.Common;
using Themia.ETax.Data;
using Themia.Framework.Data.Sequences;
using Xunit;

namespace Themia.ETax.Withholding.Data.Tests;

public sealed class SequenceWithholdingNumberSourceTests
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

    private SequenceWithholdingNumberSource Sut(Action<WithholdingNumberOptions>? configure = null)
    {
        var options = new WithholdingNumberOptions();
        configure?.Invoke(options);
        return new SequenceWithholdingNumberSource(_allocator, options);
    }

    [Fact]
    public async Task NextNumberAsync_ShouldPrintTheBuddhistYear_ButKeyTheCounterByTheGregorianYear()
    {
        _allocator.Next = 123;

        var number = await Sut().NextNumberAsync(new NoTx(), "payer-1", Date);

        Assert.Equal("WHT-2569-000123", number);
        Assert.Equal(("DocNo:ETax:Withholding:payer-1:2026", false), Assert.Single(_allocator.Calls));
    }

    [Fact]
    public async Task NextNumberAsync_ShouldPrintTheGregorianYear_WhenAsked()
    {
        var number = await Sut(o => o.Year = WithholdingNumberYear.Gregorian).NextNumberAsync(new NoTx(), "p", Date);

        Assert.Equal("WHT-2026-000001", number);
        Assert.Equal("DocNo:ETax:Withholding:p:2026", _allocator.Calls[0].Key);
    }

    [Fact]
    public async Task NextNumberAsync_ShouldKeepTheSameCounterKey_WhateverTheYearStyle()
    {
        await Sut().NextNumberAsync(new NoTx(), "p", Date);
        await Sut(o => o.Year = WithholdingNumberYear.Gregorian).NextNumberAsync(new NoTx(), "p", Date);

        Assert.Equal(_allocator.Calls[0].Key, _allocator.Calls[1].Key);
    }

    [Theory]
    [InlineData(TaxDocumentNumberPeriod.Never, "WHT-000001", "DocNo:ETax:Withholding:p")]
    [InlineData(TaxDocumentNumberPeriod.Yearly, "WHT-2569-000001", "DocNo:ETax:Withholding:p:2026")]
    [InlineData(TaxDocumentNumberPeriod.Monthly, "WHT-256910-000001", "DocNo:ETax:Withholding:p:202610")]
    public async Task NextNumberAsync_ShouldRestartTheRunPerPeriod(TaxDocumentNumberPeriod period, string expected, string key)
    {
        var number = await Sut(o => o.Period = period).NextNumberAsync(new NoTx(), "p", Date);

        Assert.Equal(expected, number);
        Assert.Equal(key, _allocator.Calls[0].Key);
    }

    [Fact]
    public async Task NextNumberAsync_ShouldRollIntoTheNextYear_OnTheFirstOfJanuary()
    {
        var number = await Sut().NextNumberAsync(new NoTx(), "p", new DateOnly(2027, 1, 1));

        Assert.Equal("WHT-2570-000001", number);
        Assert.Equal("DocNo:ETax:Withholding:p:2027", _allocator.Calls[0].Key);
    }

    [Fact]
    public async Task NextNumberAsync_ShouldUseTheHostCounters_WhenScopedToTheHost()
    {
        await Sut(o => o.Scope = TaxDocumentNumberScope.Host).NextNumberAsync(new NoTx(), "p", Date);

        Assert.True(Assert.Single(_allocator.Calls).Host);
    }

    [Fact]
    public async Task NextNumberAsync_ShouldHonourCustomPrefixAndPadding()
    {
        var number = await Sut(o => { o.Prefix = "CERT"; o.Padding = 4; }).NextNumberAsync(new NoTx(), "p", Date);

        Assert.Equal("CERT-2569-0001", number);
    }

    [Fact]
    public async Task NextNumberAsync_ShouldBuildAnyPrefixYearRunningShape_TheAdopterChooses()
    {
        var number = await Sut(o => { o.Prefix = "ES"; o.Padding = 4; o.Year = WithholdingNumberYear.Gregorian; })
            .NextNumberAsync(new NoTx(), "p", new DateOnly(2026, 3, 1));

        Assert.Equal("ES-2026-0001", number);
    }

    [Fact]
    public async Task NextNumberAsync_ShouldRunPerYearOfTheIssueDate_NotOfTheCallDate()
    {
        _allocator.Next = 1;
        await Sut().NextNumberAsync(new NoTx(), "p", new DateOnly(2026, 12, 31));
        await Sut().NextNumberAsync(new NoTx(), "p", new DateOnly(2027, 1, 1));

        Assert.Equal(["DocNo:ETax:Withholding:p:2026", "DocNo:ETax:Withholding:p:2027"], _allocator.Calls.Select(c => c.Key));
    }

    [Fact]
    public async Task NextNumberAsync_ShouldNotShareARunWithTaxDocuments()
    {
        await Sut().NextNumberAsync(new NoTx(), "payer-1", Date);

        Assert.DoesNotContain("TaxInvoiceReceipt", _allocator.Calls[0].Key, StringComparison.Ordinal);
        Assert.StartsWith("DocNo:ETax:Withholding:", _allocator.Calls[0].Key, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NextNumberAsync_ShouldRefuseAPayerIdThatOverflowsTheKeyLimit_WithoutAllocating()
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(() => Sut().NextNumberAsync(new NoTx(), new string('x', 90), Date));

        Assert.Equal("payerId", ex.ParamName);
        Assert.Empty(_allocator.Calls);
    }

    [Fact]
    public async Task NextNumberAsync_ShouldRejectBadArguments()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(() => Sut().NextNumberAsync(null!, "p", Date));
        await Assert.ThrowsAnyAsync<ArgumentException>(() => Sut().NextNumberAsync(new NoTx(), " ", Date));
        Assert.Empty(_allocator.Calls);
    }

    [Fact]
    public void Constructor_ShouldRejectNullsAndInvalidOptions()
    {
        Assert.Throws<ArgumentNullException>(() => new SequenceWithholdingNumberSource(null!, new WithholdingNumberOptions()));
        Assert.Throws<ArgumentNullException>(() => new SequenceWithholdingNumberSource(_allocator, null!));
        Assert.Throws<InvalidOperationException>(() => Sut(o => o.Padding = 0));
        Assert.Throws<InvalidOperationException>(() => Sut(o => o.Padding = 19));
        Assert.Throws<InvalidOperationException>(() => Sut(o => o.Prefix = " "));
        Assert.Throws<InvalidOperationException>(() => Sut(o => o.Period = (TaxDocumentNumberPeriod)9));
        Assert.Throws<InvalidOperationException>(() => Sut(o => o.Year = (WithholdingNumberYear)9));
    }
}
