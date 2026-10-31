using Themia.Modules.Identity.Hashing;
using Xunit;

namespace Themia.Modules.Identity.Tests.Hashing;

public sealed class RefreshSuccessorProtectorTests
{
    private const string ParentRaw = "parent-raw-token-0123456789abcdefghijklmnopqrstuv";
    private const string SuccessorRaw = "successor-raw-token-0123456789abcdefghijklmnopqrs";
    private static readonly Guid ParentId = Guid.NewGuid();

    [Fact]
    public void A_sealed_successor_opens_with_the_parent_raw_and_row_id()
    {
        var sealedText = RefreshSuccessorProtector.Protect(ParentRaw, ParentId, SuccessorRaw);

        Assert.Equal(SuccessorRaw, RefreshSuccessorProtector.Unprotect(ParentRaw, ParentId, sealedText));
    }

    [Fact]
    public void The_sealed_text_does_not_contain_the_successor_and_differs_on_every_call()
    {
        var first = RefreshSuccessorProtector.Protect(ParentRaw, ParentId, SuccessorRaw);
        var second = RefreshSuccessorProtector.Protect(ParentRaw, ParentId, SuccessorRaw);

        Assert.DoesNotContain(SuccessorRaw, first);
        Assert.NotEqual(first, second);
        Assert.True(first.Length <= 256, "fits the column");
    }

    [Fact]
    public void Another_parent_raw_or_row_id_opens_nothing()
    {
        var sealedText = RefreshSuccessorProtector.Protect(ParentRaw, ParentId, SuccessorRaw);

        Assert.Null(RefreshSuccessorProtector.Unprotect(ParentRaw + "x", ParentId, sealedText));
        Assert.Null(RefreshSuccessorProtector.Unprotect(ParentRaw, Guid.NewGuid(), sealedText));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not base64 !!")]
    [InlineData("AAAA")]
    public void Malformed_input_opens_nothing(string sealedText)
    {
        Assert.Null(RefreshSuccessorProtector.Unprotect(ParentRaw, ParentId, sealedText));
    }

    [Fact]
    public void A_tampered_value_opens_nothing()
    {
        var sealedText = RefreshSuccessorProtector.Protect(ParentRaw, ParentId, SuccessorRaw);
        var tampered = sealedText[..^2] + (sealedText.EndsWith("AA", StringComparison.Ordinal) ? "BB" : "AA");

        Assert.Null(RefreshSuccessorProtector.Unprotect(ParentRaw, ParentId, tampered));
    }
}
