using Xunit;

namespace Themia.ETax.Tests;

public sealed class CharacterValidationTests
{
    private const string Uncovered = "中文公司";

    public static TheoryData<string, Func<TaxDocumentSnapshot, TaxDocumentSnapshot>> Fields => new()
    {
        { "document_number", s => s with { DocumentNumber = "TAX-" + Uncovered } },
        { "seller.name", s => s with { Seller = s.Seller with { Name = Uncovered } } },
        { "seller.address", s => s with { Seller = s.Seller with { Address = Uncovered } } },
        { "buyer.name", s => s with { Buyer = s.Buyer with { Name = Uncovered } } },
        { "buyer.address", s => s with { Buyer = s.Buyer with { Address = Uncovered } } },
        { "lines[1].description", s => s with { Lines = [s.Lines[0], new TaxDocumentLine { Description = Uncovered, AmountExVatSatang = 0 }] } },
    };

    [Theory]
    [MemberData(nameof(Fields))]
    public void Validate_ShouldRejectWithTheFieldPathOnly_WhenAFieldHasAnUncoveredCharacter(
        string field, Func<TaxDocumentSnapshot, TaxDocumentSnapshot> mutate)
    {
        var errors = TaxDocumentSnapshotValidator.Validate(mutate(Samples.Receipt()));

        var error = Assert.Single(errors);
        Assert.Equal(TaxDocumentValidationCode.UnsupportedCharacter, error.Code);
        Assert.Equal(field, error.Field);
        Assert.DoesNotContain("中", error.ToString());
    }

    public static TheoryData<string, Func<TaxDocumentSnapshot, TaxDocumentSnapshot>> NoteFields => new()
    {
        { "note.original_document_number", s => s with { Note = s.Note! with { OriginalDocumentNumber = Uncovered } } },
        { "note.reason_code", s => s with { Note = s.Note! with { ReasonCode = Uncovered } } },
        { "note.reason", s => s with { Note = s.Note! with { Reason = Uncovered } } },
        { "replacement.original_document_number", s => s with { Replacement = Replacement() with { OriginalDocumentNumber = Uncovered } } },
        { "replacement.reason", s => s with { Replacement = Replacement() with { Reason = Uncovered } } },
    };

    [Theory]
    [MemberData(nameof(NoteFields))]
    public void Validate_ShouldRejectWithTheFieldPathOnly_WhenANoteOrReplacementFieldHasAnUncoveredCharacter(
        string field, Func<TaxDocumentSnapshot, TaxDocumentSnapshot> mutate)
    {
        var errors = TaxDocumentSnapshotValidator.Validate(mutate(Samples.FullCancellationCreditNote()));

        var error = Assert.Single(errors);
        Assert.Equal(TaxDocumentValidationCode.UnsupportedCharacter, error.Code);
        Assert.Equal(field, error.Field);
    }

    [Fact]
    public void Validate_ShouldAccept_WhenTextIsThaiBasicLatinAndLatinExtended()
    {
        var snapshot = Samples.Receipt() with
        {
            Buyer = new TaxParty { Name = "Café Łódź Ltd. บริษัท", TaxId = Samples.BuyerTaxId, Address = "Ő utca 5\nBudapest" },
        };

        Assert.Empty(TaxDocumentSnapshotValidator.Validate(snapshot));
    }

    [Fact]
    public void Validate_ShouldAccept_WhenThaiTextPastedFromTheWebCarriesZeroWidthSpaces()
    {
        var snapshot = Samples.Receipt() with { Buyer = new TaxParty { Name = "บริษัท\u200Bตัวอย่าง\uFEFF", TaxId = Samples.BuyerTaxId } };

        Assert.Empty(TaxDocumentSnapshotValidator.Validate(snapshot));
    }

    private static TaxDocumentReplacement Replacement() => new()
    {
        ReplacesDocumentId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
        OriginalDocumentNumber = "TAX-2026-000000",
        OriginalIssueDate = new DateOnly(2026, 10, 13),
        Reason = "Wrong buyer address",
    };
}
