using Themia.Challenges.Internal;
using Xunit;

namespace Themia.Challenges.Tests;

public class SecretHasherTests
{
    [Fact]
    public void Verify_ShouldAcceptTheOriginalSecret()
    {
        var (hash, salt) = SecretHasher.Hash("483920");

        Assert.True(SecretHasher.Verify("483920", hash, salt));
    }

    [Fact]
    public void Verify_ShouldRejectADifferentSecret()
    {
        var (hash, salt) = SecretHasher.Hash("483920");

        Assert.False(SecretHasher.Verify("483921", hash, salt));
    }

    // The stored hash must not be the secret, and two identical secrets must not produce identical rows.
    [Fact]
    public void Hash_ShouldNotContainThePlaintext_AndShouldSaltPerCall()
    {
        var (hashA, saltA) = SecretHasher.Hash("483920");
        var (hashB, saltB) = SecretHasher.Hash("483920");

        Assert.DoesNotContain("483920", hashA, StringComparison.Ordinal);
        Assert.NotEqual(saltA, saltB);
        Assert.NotEqual(hashA, hashB);
    }
}
