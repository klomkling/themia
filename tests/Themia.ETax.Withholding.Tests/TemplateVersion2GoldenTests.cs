using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace Themia.ETax.Withholding.Tests;

/// <summary>
/// Template version "2" is what certificates issued from now on are re-rendered with. Same contract as <see cref="TemplateVersion1GoldenTests"/>:
/// a failure means ship version "3" and leave "2" untouched; it never means update the hash.
/// </summary>
public sealed class TemplateVersion2GoldenTests
{
    public static TheoryData<string, string> Documents => new()
    {
        { "single-copy1", "85bec428cb542864724805b97b026b262445105a9842f52ddf14bc98d234ca80" },
        { "single-copy2", "85835cd5a6ca7fa0e6df56d085013e53836c5d46a7414b310627a5c1c246324b" },
        { "everything-copy1", "185770cc68b44b10320d0b0c32a7d4d87fb136f21afb0013548bc80da0a3d5ce" },
        { "single-replacement-copy", "b28f99a1015fef55b2e5d399cf70d8ec5264ceee300653d9c2c3c78ca4a62f4f" },
        { "single-cancelled", "93d5bd589616a085296423ba857c3f3278fc58324eea67d9d0e5b38d1489d2f7" },
        { "single-signed-png", "66b1a6d2b0a96b655ff510fc77c9ceddd747320d72a708138f3450800a00d02e" },
        { "everything-signed-jpeg", "d103e87ee50f43664e9498932756e6059b1ae39a3e948e03cc83c1ee74fec0e4" },
    };

    [Theory]
    [MemberData(nameof(Documents))]
    public void Html_ShouldMatchTheGolden_BecauseVersion2MustNeverChange(string name, string expectedSha256)
    {
        var (snapshot, copy, mode, signature) = TemplateVersion1GoldenTests.Fixture(name);
        var html = new WithholdingCertificateRenderer(new TemplateVersion1GoldenTests.NoPdf()).RenderHtml(snapshot, "2", copy, mode, signature);

        Assert.Equal(expectedSha256, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(html))).ToLowerInvariant());
    }

    [Fact]
    public void Version2_ShouldDifferFromVersion1_AndKeepEveryVersion1Label()
    {
        var (snapshot, copy, mode, signature) = TemplateVersion1GoldenTests.Fixture("everything-copy1");
        var renderer = new WithholdingCertificateRenderer(new TemplateVersion1GoldenTests.NoPdf());

        var v1 = renderer.RenderHtml(snapshot, "1", copy, mode, signature);
        var v2 = renderer.RenderHtml(snapshot, "2", copy, mode, signature);

        Assert.NotEqual(v1, v2);
        Assert.Contains("10,000", v2);
        Assert.Contains("class=\"cb on\"", v2);
    }
}
