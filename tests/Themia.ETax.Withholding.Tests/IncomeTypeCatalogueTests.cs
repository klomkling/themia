using Xunit;

namespace Themia.ETax.Withholding.Tests;

public class IncomeTypeCatalogueTests
{
    // Changing how the catalogue is canonicalised (or any fixture entry) changes this value. A shipped catalogue version
    // is immutable: edit nothing in place, publish a new Version, and pin the new hash beside the old one.
    private const string FixtureHash = "40dad525460359d4b53f636e9c41a691c2b79a5e1fb2b1e28308a839d92b7fd7";

    [Fact]
    public void ContentHash_ShouldBePinned_ForTheFixtureCatalogue() =>
        Assert.Equal(FixtureHash, Samples.Catalogue().ContentHash);

    [Fact]
    public void ContentHash_ShouldNotDependOnTheOrderOfEntriesOrAllowedLists()
    {
        var original = Samples.Catalogue();
        var reordered = new IncomeTypeCatalogue(
            original.Version,
            original.Entries.Reverse().Select(e => e with
            {
                AllowedFilingForms = e.AllowedFilingForms.Reverse().ToArray(),
                AllowedPayeeKinds = e.AllowedPayeeKinds.Reverse().ToArray(),
            }).ToArray());

        Assert.Equal(original.ContentHash, reordered.ContentHash);
    }

    [Fact]
    public void ContentHash_ShouldChange_WhenAnEntryOrTheVersionChanges()
    {
        var original = Samples.Catalogue();
        var edited = original.Entries.Select(e => e.Code == "fixture-fee" ? e with { FormRow = 3 } : e).ToArray();

        Assert.NotEqual(original.ContentHash, new IncomeTypeCatalogue(original.Version, edited).ContentHash);
        Assert.NotEqual(original.ContentHash, new IncomeTypeCatalogue("test-2", original.Entries).ContentHash);
    }

    [Fact]
    public void TryGet_ShouldFindAnEntryByCode_AndMissOtherwise()
    {
        var catalogue = Samples.Catalogue();

        Assert.True(catalogue.TryGet("fixture-fee", out var entry));
        Assert.Equal("40(2)", entry!.Section);
        Assert.False(catalogue.TryGet("nope", out _));
    }

    [Fact]
    public void Constructor_ShouldRejectDuplicateCodes()
    {
        var entry = Samples.Catalogue().Entries[0];
        Assert.Throws<ArgumentException>(() => new IncomeTypeCatalogue("v", [entry, entry]));
    }

    [Theory]
    [InlineData("")]
    [InlineData(" ")]
    public void Constructor_ShouldRejectABlankVersion(string version) =>
        Assert.Throws<ArgumentException>(() => new IncomeTypeCatalogue(version, Samples.Catalogue().Entries));

    [Fact]
    public void Constructor_ShouldRejectNoEntries() =>
        Assert.Throws<ArgumentException>(() => new IncomeTypeCatalogue("v", []));

    [Theory]
    [InlineData("Upper")]
    [InlineData("has space")]
    [InlineData("")]
    public void Constructor_ShouldRejectAnUnstableCode(string code)
    {
        var entry = Samples.Catalogue().Entries[0] with { Code = code };
        Assert.Throws<ArgumentException>(() => new IncomeTypeCatalogue("v", [entry]));
    }

    [Fact]
    public void Constructor_ShouldRejectAnEntryWithNothingAllowed()
    {
        var entry = Samples.Catalogue().Entries[0];
        Assert.Throws<ArgumentException>(() => new IncomeTypeCatalogue("v", [entry with { AllowedFilingForms = [] }]));
        Assert.Throws<ArgumentException>(() => new IncomeTypeCatalogue("v", [entry with { AllowedPayeeKinds = [] }]));
    }

    [Fact]
    public void Constructor_ShouldRejectAnUnprintableRowOrBlankLabel()
    {
        var entry = Samples.Catalogue().Entries[0];
        Assert.Throws<ArgumentException>(() => new IncomeTypeCatalogue("v", [entry with { FormRow = 0 }]));
        Assert.Throws<ArgumentException>(() => new IncomeTypeCatalogue("v", [entry with { LabelTh = " " }]));
        Assert.Throws<ArgumentException>(() => new IncomeTypeCatalogue("v", [entry with { Section = "" }]));
    }

    [Fact]
    public void Constructor_ShouldRejectUndefinedEnumValues()
    {
        var entry = Samples.Catalogue().Entries[0];
        Assert.Throws<ArgumentException>(() => new IncomeTypeCatalogue("v", [entry with { AllowedFilingForms = [(WithholdingFilingForm)99] }]));
        Assert.Throws<ArgumentException>(() => new IncomeTypeCatalogue("v", [entry with { AllowedPayeeKinds = [(WithholdingPayeeKind)99] }]));
    }
}
