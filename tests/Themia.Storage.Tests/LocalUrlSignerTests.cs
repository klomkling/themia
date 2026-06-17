using Themia.Storage.Local;
using Xunit;

namespace Themia.Storage.Tests;

public sealed class LocalUrlSignerTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.Parse("2026-06-17T00:00:00Z");
    private readonly LocalUrlSigner signer = new("the-signing-key-32-bytes-minimum!");

    [Fact]
    public void Valid_signature_within_expiry_verifies()
    {
        var token = signer.Sign("tenant/a.txt", "get", Now.AddMinutes(10));
        Assert.True(signer.TryVerify("tenant/a.txt", "get", token, Now));
    }

    [Fact]
    public void Expired_signature_fails()
    {
        var token = signer.Sign("tenant/a.txt", "get", Now.AddMinutes(-1));
        Assert.False(signer.TryVerify("tenant/a.txt", "get", token, Now));
    }

    [Fact]
    public void Tampered_key_fails()
    {
        var token = signer.Sign("tenant/a.txt", "get", Now.AddMinutes(10));
        Assert.False(signer.TryVerify("tenant/OTHER.txt", "get", token, Now));
    }
}
