using Themia.Modules.Identity.Hashing;
using Xunit;

namespace Themia.Modules.Identity.Tests.Hashing;

public class TokenHasherTests
{
    [Fact]
    public void Hash_is_deterministic_for_the_same_token()
    {
        Assert.Equal(TokenHasher.Hash("abc"), TokenHasher.Hash("abc"));
    }

    [Fact]
    public void Hash_differs_for_different_tokens()
    {
        Assert.NotEqual(TokenHasher.Hash("abc"), TokenHasher.Hash("abd"));
    }

    [Fact]
    public void Matches_is_true_only_for_the_right_token()
    {
        var hash = TokenHasher.Hash("token-value");
        Assert.True(TokenHasher.Matches(hash, "token-value"));
        Assert.False(TokenHasher.Matches(hash, "other"));
    }
}
