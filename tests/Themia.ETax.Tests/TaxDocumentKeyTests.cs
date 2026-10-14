using Xunit;

namespace Themia.ETax.Tests;

public class TaxDocumentKeyTests
{
    [Fact]
    public void Key_ShouldDiffer_ForTwoPartialRefundsOnTheSamePayment()
    {
        // F1: a credit note is keyed by the REFUND id, not the payment id.
        var first = new TaxDocumentKey(TaxDocumentKind.CreditNote, "refund-1");
        var second = new TaxDocumentKey(TaxDocumentKind.CreditNote, "refund-2");

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Key_ShouldBeEqual_ForARepeatOfTheSameEvent()
    {
        Assert.Equal(
            new TaxDocumentKey(TaxDocumentKind.CreditNote, "refund-1"),
            new TaxDocumentKey(TaxDocumentKind.CreditNote, "refund-1"));
    }

    [Fact]
    public void Key_ShouldDiffer_ByKindAndRevision()
    {
        var receipt = new TaxDocumentKey(TaxDocumentKind.TaxInvoiceReceipt, "pay-1");
        Assert.NotEqual(receipt, new TaxDocumentKey(TaxDocumentKind.DebitNote, "pay-1"));
        Assert.NotEqual(receipt, new TaxDocumentKey(TaxDocumentKind.TaxInvoiceReceipt, "pay-1", revision: 2));
        Assert.Equal(1, receipt.Revision);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Key_ShouldReject_BlankSourceEventId(string id)
    {
        Assert.Throws<ArgumentException>(() => new TaxDocumentKey(TaxDocumentKind.CreditNote, id));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Key_ShouldReject_RevisionBelowOne(int revision)
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TaxDocumentKey(TaxDocumentKind.CreditNote, "r", revision));
    }

    [Fact]
    public void Key_ShouldReject_UndefinedKind()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new TaxDocumentKey((TaxDocumentKind)99, "r"));
    }
}
