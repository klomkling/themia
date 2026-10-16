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

        /// <summary>When set, every key counts from 1 on its own, as the real allocator does.</summary>
        public bool PerKey { get; set; }

        private readonly Dictionary<string, long> _perKey = [];

        private long Take(string key)
        {
            if (!PerKey)
                return Next++;
            _perKey[key] = _perKey.GetValueOrDefault(key) + 1;
            return _perKey[key];
        }

        public Task<long> NextInTransactionAsync(DbTransaction tx, string sequenceKey, CancellationToken ct = default)
        {
            Calls.Add((sequenceKey, false));
            return Task.FromResult(Take(sequenceKey));
        }

        public Task<long> NextHostInTransactionAsync(DbTransaction tx, string sequenceKey, CancellationToken ct = default)
        {
            Calls.Add((sequenceKey, true));
            return Task.FromResult(Take(sequenceKey));
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
    public async Task NextNumberAsync_ShouldDefaultToPrefixYearSeq_WithTheBuddhistYear_AndKeyTheCounterByTheGregorianYear()
    {
        _allocator.Next = 123;

        var number = await Sut().NextNumberAsync(new NoTx(), "payer-1", Date);

        Assert.Equal("WHT-2569-000123", number);
        Assert.Equal(("DocNo:ETax:Withholding:payer-1:2026", false), Assert.Single(_allocator.Calls));
    }

    [Theory]
    [InlineData(WithholdingNumberShape.PrefixYearSeq, WithholdingNumberYear.BuddhistEra, "WHT-2569-000010")]
    [InlineData(WithholdingNumberShape.PrefixYearSeq, WithholdingNumberYear.Gregorian, "WHT-2026-000010")]
    [InlineData(WithholdingNumberShape.SeqSlashYear2, WithholdingNumberYear.BuddhistEra, "000010/69")]
    [InlineData(WithholdingNumberShape.SeqSlashYear2, WithholdingNumberYear.Gregorian, "000010/26")]
    [InlineData(WithholdingNumberShape.YearSeq, WithholdingNumberYear.BuddhistEra, "2569-000010")]
    [InlineData(WithholdingNumberShape.YearSeq, WithholdingNumberYear.Gregorian, "2026-000010")]
    public async Task NextNumberAsync_ShouldPrintTheChosenShapeAndYearStyle_WithASixDigitSequence(
        WithholdingNumberShape shape, WithholdingNumberYear year, string expected)
    {
        _allocator.Next = 10;

        var number = await Sut(o => { o.Shape = shape; o.Year = year; }).NextNumberAsync(new NoTx(), "p", Date);

        Assert.Equal(expected, number);
    }

    [Fact]
    public async Task NextNumberAsync_ShouldKeepTheSameCounterKey_WhateverTheShapeOrYearStyle()
    {
        await Sut().NextNumberAsync(new NoTx(), "p", Date);
        await Sut(o => { o.Shape = WithholdingNumberShape.SeqSlashYear2; o.Year = WithholdingNumberYear.Gregorian; }).NextNumberAsync(new NoTx(), "p", Date);
        await Sut(o => o.Shape = WithholdingNumberShape.YearSeq).NextNumberAsync(new NoTx(), "p", Date);

        Assert.Single(_allocator.Calls.Select(c => c.Key).Distinct());
    }

    [Theory]
    [InlineData(TaxDocumentNumberPeriod.Never, "DocNo:ETax:Withholding:p")]
    [InlineData(TaxDocumentNumberPeriod.Yearly, "DocNo:ETax:Withholding:p:2026")]
    [InlineData(TaxDocumentNumberPeriod.Monthly, "DocNo:ETax:Withholding:p:202610")]
    public async Task NextNumberAsync_ShouldRestartTheRunPerPeriod(TaxDocumentNumberPeriod period, string key)
    {
        await Sut(o => { o.Period = period; o.Shape = WithholdingNumberShape.PrefixYearMonthSeq; }).NextNumberAsync(new NoTx(), "p", Date);

        Assert.Equal(key, _allocator.Calls[0].Key);
    }

    [Theory]
    [InlineData(WithholdingNumberYear.BuddhistEra, "WHT-6910-000001", "WHT-6911-000001")]
    [InlineData(WithholdingNumberYear.Gregorian, "WHT-2610-000001", "WHT-2611-000001")]
    public async Task NextNumberAsync_ShouldPrintTheMonthAndStartAgainEachMonth_ForThePrefixYearMonthShape(
        WithholdingNumberYear year, string october, string november)
    {
        _allocator.PerKey = true;
        var sut = Sut(o => { o.Shape = WithholdingNumberShape.PrefixYearMonthSeq; o.Period = TaxDocumentNumberPeriod.Monthly; o.Year = year; });

        var last = await sut.NextNumberAsync(new NoTx(), "p", new DateOnly(2026, 10, 31));
        var first = await sut.NextNumberAsync(new NoTx(), "p", new DateOnly(2026, 11, 1));

        Assert.Equal(october, last);
        Assert.Equal(november, first);
        Assert.NotEqual(last, first);
    }

    [Theory]
    [InlineData(WithholdingNumberShape.PrefixYearSeq)]
    [InlineData(WithholdingNumberShape.SeqSlashYear2)]
    [InlineData(WithholdingNumberShape.YearSeq)]
    public void Constructor_ShouldRejectAMonthlyRun_ForAShapeThatPrintsOnlyTheYear(WithholdingNumberShape shape)
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Sut(o => { o.Shape = shape; o.Period = TaxDocumentNumberPeriod.Monthly; }));

        Assert.Contains("Monthly", ex.Message);
    }

    [Fact]
    public void Constructor_ShouldRejectABlankPrefix_ForThePrefixYearMonthShape()
    {
        Assert.Throws<InvalidOperationException>(() => Sut(o => { o.Shape = WithholdingNumberShape.PrefixYearMonthSeq; o.Prefix = " "; }));
    }

    [Fact]
    public async Task NextNumberAsync_ShouldRollIntoTheNextYear_OnTheFirstOfJanuary()
    {
        var number = await Sut().NextNumberAsync(new NoTx(), "p", new DateOnly(2027, 1, 1));

        Assert.Equal("WHT-2570-000001", number);
        Assert.Equal("DocNo:ETax:Withholding:p:2027", _allocator.Calls[0].Key);
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
    public async Task NextNumberAsync_ShouldUseTheHostCounters_WhenScopedToTheHost()
    {
        await Sut(o => o.Scope = TaxDocumentNumberScope.Host).NextNumberAsync(new NoTx(), "s", Date);

        Assert.True(Assert.Single(_allocator.Calls).Host);
    }

    [Fact]
    public async Task NextNumberAsync_ShouldHonourACustomPrefix_OnlyForThePrefixShape()
    {
        Assert.Equal("ES-2569-000001", await Sut(o => o.Prefix = "ES").NextNumberAsync(new NoTx(), "p", Date));
        _allocator.Next = 1;
        Assert.Equal("2569-000001", await Sut(o => { o.Shape = WithholdingNumberShape.YearSeq; o.Prefix = " "; }).NextNumberAsync(new NoTx(), "p", Date));
    }

    [Fact]
    public async Task NextNumberAsync_ShouldNotTruncateASequenceWiderThanSixDigits()
    {
        _allocator.Next = 1_234_567;

        Assert.Equal("WHT-2569-1234567", await Sut().NextNumberAsync(new NoTx(), "p", Date));
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
        Assert.Throws<InvalidOperationException>(() => Sut(o => o.Prefix = " "));
        Assert.Throws<InvalidOperationException>(() => Sut(o => o.Shape = (WithholdingNumberShape)9));
        Assert.Throws<InvalidOperationException>(() => Sut(o => o.Period = (TaxDocumentNumberPeriod)9));
        Assert.Throws<InvalidOperationException>(() => Sut(o => o.Year = (WithholdingNumberYear)9));
    }

    // ---- series ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Series_ShouldGetItsOwnRunKey_WithoutChangingTheKeyOfTheUnseriesRun()
    {
        var sut = Sut();

        await sut.NextNumberAsync(new NoTx(), "p", Date);
        await sut.NextNumberInSeriesAsync(new NoTx(), "p", null, Date);
        await sut.NextNumberInSeriesAsync(new NoTx(), "p", "CR", Date);
        await sut.NextNumberInSeriesAsync(new NoTx(), "p", "SF", Date);

        Assert.Equal(
            ["DocNo:ETax:Withholding:p:2026", "DocNo:ETax:Withholding:p:2026", "DocNo:ETax:Withholding:p:series=CR:2026", "DocNo:ETax:Withholding:p:series=SF:2026"],
            _allocator.Calls.Select(c => c.Key));
    }

    [Fact]
    public async Task Series_ShouldNotCollideWithAPeriod_EvenWhenItLooksLikeOne()
    {
        var sut = Sut();

        await sut.NextNumberInSeriesAsync(new NoTx(), "p", "2026", Date);
        await sut.NextNumberAsync(new NoTx(), "p", Date);

        Assert.NotEqual(_allocator.Calls[0].Key, _allocator.Calls[1].Key);
    }

    [Fact]
    public async Task Series_ShouldNotChangeWhatIsPrinted_OnlyTheRun()
    {
        _allocator.Next = 5;

        Assert.Equal("WHT-2569-000005", await Sut().NextNumberInSeriesAsync(new NoTx(), "p", "CR", Date));
    }

    [Fact]
    public async Task Series_ShouldBeRefusedWhenMalformedOrTooLongForTheKey_WithoutAllocating()
    {
        var sut = Sut();

        await Assert.ThrowsAsync<ArgumentException>(() => sut.NextNumberInSeriesAsync(new NoTx(), "p", "C:R", Date));
        await Assert.ThrowsAsync<ArgumentException>(() => sut.NextNumberInSeriesAsync(new NoTx(), "p", "", Date));
        await Assert.ThrowsAsync<ArgumentException>(() => sut.NextNumberInSeriesAsync(new NoTx(), "p", new string('x', 90), Date));
        Assert.Empty(_allocator.Calls);
    }
}
