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
        { "single-copy1", "0412bb1c14d5b5f26e2857d30e8809fc63354ac190a15f935cf846b448aed766" },
        { "single-copy2", "d44cacf317aa2d71d7e394004d5740960df5327b6ba5c5784164402a1b830bab" },
        { "everything-copy1", "86401a2f3b2e63addadb05a0bb9f88b51d23426d44377ed7ce8be4164ab4b5e1" },
        { "single-replacement-copy", "ed6dc1250d0c251189a374a5df512186d60cd21d839b3b20dcb7fd2f888ee899" },
        { "single-cancelled", "59ed437d4e03347c079f665e8620cb5892a17fffb19ace2414d04ad515c982c7" },
        { "single-signed-png", "671419539b4ea2102a749fe6ca168694ec67c1b0bed305fd414cbabc47650fae" },
        { "everything-signed-jpeg", "3c3bb8ead9fa8cfec0f5a961ca40fe86c9def50d457fe12ea5531c2a7ae1e760" },
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
