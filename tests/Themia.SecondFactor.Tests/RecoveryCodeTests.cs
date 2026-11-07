using System.Security.Cryptography;

using Themia.SecondFactor;

using Xunit;

namespace Themia.SecondFactor.Tests;

public sealed class RecoveryCodeTests
{
    private static readonly byte[] Key = RandomNumberGenerator.GetBytes(32);

    [Fact]
    public void Generate_ShouldReturnDistinctFormattedCodes()
    {
        var codes = RecoveryCodeGenerator.Generate(10);

        Assert.Equal(10, codes.Distinct().Count());
        Assert.All(codes, code => Assert.Matches("^[A-Z2-9]{4}-[A-Z2-9]{4}-[A-Z2-9]{4}$", code));
    }

    [Fact]
    public void Hash_ShouldIgnoreCaseAndDashes()
    {
        var hasher = new RecoveryCodeHasher(Key);

        Assert.Equal(hasher.Hash("u1", "ABCD-EFGH-JKMN"), hasher.Hash("u1", "abcdefghjkmn"));
    }

    [Fact]
    public void Hash_ShouldDifferPerUser()
    {
        var hasher = new RecoveryCodeHasher(Key);

        Assert.NotEqual(hasher.Hash("u1", "ABCD-EFGH-JKMN"), hasher.Hash("u2", "ABCD-EFGH-JKMN"));
    }

    [Fact]
    public void Hash_ShouldDifferPerKey()
    {
        var other = new RecoveryCodeHasher(RandomNumberGenerator.GetBytes(32));

        Assert.NotEqual(new RecoveryCodeHasher(Key).Hash("u1", "ABCD-EFGH-JKMN"), other.Hash("u1", "ABCD-EFGH-JKMN"));
    }

    [Fact]
    public void Constructor_ShouldRefuseShortKey()
    {
        Assert.Throws<ArgumentException>(() => new RecoveryCodeHasher(new byte[16]));
    }
}
