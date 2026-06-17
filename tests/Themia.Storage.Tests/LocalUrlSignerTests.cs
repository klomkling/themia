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
        var token = signer.Sign("tenant/a.txt", PresignedUrlOperation.Get, Now.AddMinutes(10));
        Assert.True(signer.TryVerify("tenant/a.txt", PresignedUrlOperation.Get, token, Now));
    }

    [Fact]
    public void Expired_signature_fails()
    {
        var token = signer.Sign("tenant/a.txt", PresignedUrlOperation.Get, Now.AddMinutes(-1));
        Assert.False(signer.TryVerify("tenant/a.txt", PresignedUrlOperation.Get, token, Now));
    }

    [Fact]
    public void Tampered_key_fails()
    {
        var token = signer.Sign("tenant/a.txt", PresignedUrlOperation.Get, Now.AddMinutes(10));
        Assert.False(signer.TryVerify("tenant/OTHER.txt", PresignedUrlOperation.Get, token, Now));
    }

    [Fact]
    public void Operation_mismatch_fails()
    {
        var token = signer.Sign("tenant/a.txt", PresignedUrlOperation.Get, Now.AddMinutes(10));
        Assert.False(signer.TryVerify("tenant/a.txt", PresignedUrlOperation.Put, token, Now));
    }
}
