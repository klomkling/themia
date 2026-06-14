using Themia.Modules.Identity.Hashing;
using Xunit;

namespace Themia.Modules.Identity.Tests.Hashing;

public class Argon2idPasswordHasherTests
{
    private readonly Argon2idPasswordHasher hasher = new();

    [Fact]
    public void Hash_is_not_plaintext_and_is_encoded()
    {
        var encoded = hasher.Hash("correct horse battery staple");

        Assert.DoesNotContain("correct horse", encoded);
        Assert.StartsWith("argon2id$", encoded);
    }

    [Fact]
    public void Hash_is_salted_so_two_hashes_of_same_password_differ()
    {
        Assert.NotEqual(hasher.Hash("pw"), hasher.Hash("pw"));
    }

    [Fact]
    public void Verify_succeeds_for_correct_password()
    {
        var encoded = hasher.Hash("s3cret");
        Assert.True(hasher.Verify(encoded, "s3cret"));
    }

    [Fact]
    public void Verify_fails_for_wrong_password()
    {
        var encoded = hasher.Hash("s3cret");
        Assert.False(hasher.Verify(encoded, "wrong"));
    }

    [Fact]
    public void Verify_returns_false_for_malformed_hash_instead_of_throwing()
    {
        Assert.False(hasher.Verify("not-a-valid-hash", "pw"));
    }

    [Fact]
    public void NeedsRehash_is_false_for_a_freshly_made_hash()
    {
        var encoded = hasher.Hash("pw");
        Assert.False(hasher.NeedsRehash(encoded));
    }

    [Fact]
    public void NeedsRehash_is_true_for_weaker_parameters()
    {
        var weak = "argon2id$v=19$m=1024,t=1,p=1$c2FsdHNhbHQ=$aGFzaGhhc2g=";
        Assert.True(hasher.NeedsRehash(weak));
    }
}
