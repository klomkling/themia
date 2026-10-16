using Xunit;

namespace Themia.ETax.Withholding.Tests;

public sealed class SignatureImageTests
{
    [Fact]
    public void Constructor_ShouldAcceptPngAndJpeg_AndReportTheMediaType()
    {
        Assert.Equal("image/png", new WithholdingSignatureImage(SignatureFixtures.Png).MediaType);
        Assert.Equal("image/jpeg", new WithholdingSignatureImage(SignatureFixtures.Jpeg).MediaType);
        Assert.Equal(SignatureFixtures.Png.Length, new WithholdingSignatureImage(SignatureFixtures.Png).Length);
    }

    [Fact]
    public void Constructor_ShouldCopyTheBytes_SoALaterChangeByTheCallerCannotAlterWhatIsPrinted()
    {
        var bytes = (byte[])SignatureFixtures.Png.Clone();
        var image = new WithholdingSignatureImage(bytes);
        var before = image.ToDataUri();

        bytes[10] ^= 0xFF;

        Assert.Equal(before, image.ToDataUri());
    }

    [Fact]
    public void Constructor_ShouldRefuseEmptyOversizeAndNonImageBytes()
    {
        Assert.Throws<ArgumentException>(() => new WithholdingSignatureImage([]));
        Assert.Throws<ArgumentException>(() => new WithholdingSignatureImage("GIF89a......"u8.ToArray()));
        Assert.Throws<ArgumentException>(() => new WithholdingSignatureImage("<svg xmlns='http://www.w3.org/2000/svg'/>"u8.ToArray()));
        Assert.Throws<ArgumentException>(() => new WithholdingSignatureImage([0x89, 0x50, 0x4E, 0x47])); // a truncated PNG signature
        Assert.Throws<ArgumentNullException>(() => new WithholdingSignatureImage(null!));

        var tooBig = new byte[WithholdingSignatureImage.MaxBytes + 1];
        SignatureFixtures.Png.CopyTo(tooBig, 0);
        Assert.Throws<ArgumentException>(() => new WithholdingSignatureImage(tooBig));
    }

    [Fact]
    public void Constructor_ShouldAcceptAnImageOfExactlyTheLimit()
    {
        var atLimit = new byte[WithholdingSignatureImage.MaxBytes];
        SignatureFixtures.Png.CopyTo(atLimit, 0);

        Assert.Equal(WithholdingSignatureImage.MaxBytes, new WithholdingSignatureImage(atLimit).Length);
    }
}
