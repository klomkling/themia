using Xunit;

namespace Themia.Exceptional.Tests;

public sealed class ExceptionEntryFactoryMaskingTests
{
    private const string Jwt = "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJhZG1pbiJ9.c2lnbmF0dXJl";
    private const string App = "app";

    [Fact]
    public void FromException_MasksAJwtInTheMessage()
    {
        var entry = ExceptionEntryFactory.FromException(new InvalidOperationException($"bad token {Jwt} given"), App);

        Assert.DoesNotContain("eyJ", entry.Message);
        Assert.DoesNotContain("eyJ", entry.Detail);
    }

    [Fact]
    public void FromException_MasksAJwtThatStartsNearTheTruncationLimit()
    {
        var message = new string('x', 989) + " " + Jwt + " tail";

        var entry = ExceptionEntryFactory.FromException(new InvalidOperationException(message), App);

        Assert.DoesNotContain("eyJ", entry.Message);
        Assert.DoesNotContain("c2lnbmF0", entry.Message);
    }

    [Fact]
    public void FromException_MasksAJwtInAnInnerOfInnerException()
    {
        var deep = new InvalidOperationException($"deep {Jwt}");
        var ex = new InvalidOperationException("outer", new ArgumentException("middle", deep));

        var entry = ExceptionEntryFactory.FromException(ex, App);

        Assert.DoesNotContain("eyJ", entry.Detail);
    }

    [Fact]
    public void FromException_MasksDataEntriesByKeyAndByValueShape()
    {
        var ex = new InvalidOperationException("boom");
        ex.Data["token"] = "abc123";
        ex.Data["note"] = Jwt;
        ex.Data["orderId"] = "42";

        var entry = ExceptionEntryFactory.FromException(ex, App);

        Assert.DoesNotContain("abc123", entry.Detail);
        Assert.DoesNotContain("eyJ", entry.Detail);
        Assert.Contains("42", entry.Detail);
    }

    [Fact]
    public void FromException_KeepsAnOrdinaryMessageReadable()
    {
        var entry = ExceptionEntryFactory.FromException(new InvalidOperationException("Order 202611100042 not found"), App);

        Assert.Equal("Order 202611100042 not found", entry.Message);
    }

    [Fact]
    public void FromException_GroupsMessageOnlyExceptionsThatDifferOnlyByAToken()
    {
        var first = ExceptionEntryFactory.FromException(new InvalidOperationException($"rejected {Jwt}"), App);
        var second = ExceptionEntryFactory.FromException(
            new InvalidOperationException("rejected eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJvdGhlciJ9.b3RoZXJzaWc"), App);

        Assert.Equal(first.ErrorHash, second.ErrorHash);
    }
}
