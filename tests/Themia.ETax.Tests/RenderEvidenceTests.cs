using Xunit;

namespace Themia.ETax.Tests;

public sealed class RenderEvidenceTests
{
    private static TaxDocumentRender Render(string hash, string version) => new([1], hash, version);

    private static TaxDocumentRenderEvidence Row(string hash, string version) => new(hash, version);

    [Fact]
    public void Check_ShouldBeFirstRender_WhenThereIsNoHistory() =>
        Assert.Equal(TaxDocumentEvidenceOutcome.FirstRender,
            TaxDocumentEvidence.Check([], Render("aa", "v1|n1")));

    [Fact]
    public void Check_ShouldBeIdentical_WhenTheSameVersionProducedTheSameHash() =>
        Assert.Equal(TaxDocumentEvidenceOutcome.Identical,
            TaxDocumentEvidence.Check([Row("aa", "v1|n1")], Render("aa", "v1|n1")));

    [Fact]
    public void Check_ShouldFailLoud_WhenTheSameVersionProducedADifferentHash()
    {
        var ex = Assert.Throws<TaxDocumentRenderMismatchException>(
            () => TaxDocumentEvidence.Check([Row("aa", "v1|n1")], Render("bb", "v1|n1")));

        Assert.Equal("v1|n1", ex.RendererVersion);
    }

    [Fact]
    public void Check_ShouldBeNewRendererVersion_WhenNoRowCarriesThisVersion() =>
        Assert.Equal(TaxDocumentEvidenceOutcome.NewRendererVersion,
            TaxDocumentEvidence.Check([Row("aa", "v1|n1")], Render("bb", "v2|n1")));

    [Fact]
    public void Check_ShouldFailLoud_WhenAnyRowOfTheSameVersionDiffers()
    {
        var history = new[] { Row("aa", "v1|n1"), Row("cc", "v2|n1") };

        Assert.Throws<TaxDocumentRenderMismatchException>(() => TaxDocumentEvidence.Check(history, Render("dd", "v2|n1")));
    }

    [Fact]
    public void Check_ShouldBeNewRendererVersion_WhenOnlyTheNormaliserVersionChanged() =>
        Assert.Equal(TaxDocumentEvidenceOutcome.NewRendererVersion,
            TaxDocumentEvidence.Check([Row("aa", "v1|n1")], Render("bb", "v1|n2")));
}
