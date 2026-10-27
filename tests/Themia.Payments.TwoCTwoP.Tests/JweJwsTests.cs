using System.Security.Cryptography;
using System.Text;

using Themia.Payments.TwoCTwoP.Internal;

using Xunit;

namespace Themia.Payments.TwoCTwoP.Tests;

public sealed class JweJwsTests : IDisposable
{
    private const string Xml = "<PaymentProcessRequest><invoiceNo>INV-1</invoiceNo></PaymentProcessRequest>";

    private readonly RSA provider = RSA.Create(2048);
    private readonly RSA merchant = RSA.Create(2048);

    public void Dispose()
    {
        provider.Dispose();
        merchant.Dispose();
    }

    // The request direction is merchant -> 2C2P; the response direction is 2C2P -> merchant. Sealing with the
    // provider's key pair and the merchant's key pair swapped is exactly the response a real 2C2P would send.
    private string SealResponse(string xml) => JweJws.Seal(xml, merchant, provider);

    private bool OpenResponse(string jws, out string xml) => JweJws.TryOpen(jws, provider, merchant, out xml);

    [Fact]
    public void Seal_ShouldProduceAPs256JwsOverARsaOaepA256GcmJwe_ThatTheReceiverOpens()
    {
        var jws = JweJws.Seal(Xml, provider, merchant);

        var parts = jws.Split('.');
        Assert.Equal(3, parts.Length);
        Assert.Equal("""{"alg":"PS256","typ":"JWT"}""", Encoding.ASCII.GetString(Base64Url.Decode(parts[0])));
        var jwe = Encoding.ASCII.GetString(Base64Url.Decode(parts[1])).Split('.');
        Assert.Equal(5, jwe.Length);
        Assert.Equal("""{"alg":"RSA-OAEP","enc":"A256GCM"}""", Encoding.ASCII.GetString(Base64Url.Decode(jwe[0])));
        Assert.DoesNotContain("INV-1", Encoding.ASCII.GetString(Base64Url.Decode(parts[1])), StringComparison.Ordinal);

        Assert.True(JweJws.TryOpen(jws, merchant, provider, out var xml));
        Assert.Equal(Xml, xml);
    }

    [Fact]
    public void TryOpen_ShouldRoundTripAResponse()
    {
        Assert.True(OpenResponse(SealResponse(Xml), out var xml));
        Assert.Equal(Xml, xml);
    }

    [Fact]
    public void TryOpen_ShouldRefuseASignatureFromAnotherKey()
    {
        using var stranger = RSA.Create(2048);
        var jws = JweJws.Seal(Xml, merchant, stranger);

        Assert.False(OpenResponse(jws, out var xml));
        Assert.Equal("", xml);
    }

    [Fact]
    public void TryOpen_ShouldRefuseACiphertextForAnotherReceiver()
    {
        using var stranger = RSA.Create(2048);
        var jws = JweJws.Seal(Xml, stranger, provider);

        Assert.False(OpenResponse(jws, out _));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void TryOpen_ShouldRefuseATamperedSegment(int index)
    {
        var parts = SealResponse(Xml).Split('.');
        var bytes = Base64Url.Decode(parts[index]);
        bytes[^1] ^= 0x01;
        parts[index] = Base64Url.Encode(bytes);

        Assert.False(OpenResponse(string.Join('.', parts), out _));
    }

    [Fact]
    public void TryOpen_ShouldRefuseATamperedGcmTag_EvenUnderAValidSignature()
    {
        var jwe = Encoding.ASCII.GetString(Base64Url.Decode(SealResponse(Xml).Split('.')[1])).Split('.');
        var tag = Base64Url.Decode(jwe[4]);
        tag[0] ^= 0x01;
        jwe[4] = Base64Url.Encode(tag);

        Assert.False(OpenResponse(SignOver(string.Join('.', jwe), provider, """{"alg":"PS256","typ":"JWT"}"""), out _));
    }

    [Fact]
    public void TryOpen_ShouldRefuseATruncatedTag()
    {
        var jwe = Encoding.ASCII.GetString(Base64Url.Decode(SealResponse(Xml).Split('.')[1])).Split('.');
        jwe[4] = Base64Url.Encode(Base64Url.Decode(jwe[4])[..8]);

        Assert.False(OpenResponse(SignOver(string.Join('.', jwe), provider, """{"alg":"PS256","typ":"JWT"}"""), out _));
    }

    [Theory]
    [InlineData("""{"alg":"none"}""")]
    [InlineData("""{"alg":"RS256","typ":"JWT"}""")]
    [InlineData("""{"alg":"PS256","typ":"JWT","kid":"x"}""")]
    [InlineData("""{"alg":"PS256","jwk":{"kty":"RSA"}}""")]
    [InlineData("""{"alg":"PS256","crit":["b64"],"b64":false}""")]
    public void TryOpen_ShouldRefuseAJwsHeaderThatIsNotPinned(string header)
    {
        var jwe = Encoding.ASCII.GetString(Base64Url.Decode(SealResponse(Xml).Split('.')[1]));

        Assert.False(OpenResponse(SignOver(jwe, provider, header), out _));
    }

    [Theory]
    [InlineData("""{"alg":"RSA1_5","enc":"A256GCM"}""")]
    [InlineData("""{"alg":"RSA-OAEP","enc":"A128GCM"}""")]
    [InlineData("""{"alg":"RSA-OAEP","enc":"A256GCM","zip":"DEF"}""")]
    public void TryOpen_ShouldRefuseAJweHeaderThatIsNotPinned(string header)
    {
        var jwe = Encoding.ASCII.GetString(Base64Url.Decode(SealResponse(Xml).Split('.')[1])).Split('.');
        jwe[0] = Base64Url.Encode(Encoding.ASCII.GetBytes(header));

        Assert.False(OpenResponse(SignOver(string.Join('.', jwe), provider, """{"alg":"PS256","typ":"JWT"}"""), out _));
    }

    [Theory]
    [InlineData("")]
    [InlineData("a.b")]
    [InlineData("a.b.c.d")]
    [InlineData("not a jws at all")]
    public void TryOpen_ShouldRefuseGarbage_WithoutThrowing(string garbage) => Assert.False(OpenResponse(garbage, out _));

    private static string SignOver(string jwe, RSA signer, string header)
    {
        var input = Base64Url.Encode(Encoding.ASCII.GetBytes(header)) + "." + Base64Url.Encode(Encoding.ASCII.GetBytes(jwe));
        return input + "." + Base64Url.Encode(signer.SignData(Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
    }
}
