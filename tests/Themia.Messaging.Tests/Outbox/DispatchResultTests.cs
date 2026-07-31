using Themia.Messaging.Outbox;

using Xunit;

namespace Themia.Messaging.Tests.Outbox;

// F9: a bare-message DispatchResult carries no exception, so a dispatch failure's log line had no type,
// no stack, and no channel — undiagnosable when the underlying bug is, say, a NullReferenceException
// inside a sender. The Transient/Permanent(string, Exception) overloads must actually carry the exception
// through, while the existing string-only overloads (still used where a dispatcher reports a failure
// result rather than throwing) must keep leaving it null.
public class DispatchResultTests
{
    [Fact]
    public void Transient_WithException_ShouldCarryTheException()
    {
        var exception = new InvalidOperationException("boom");

        var result = DispatchResult.Transient("boom", exception);

        Assert.Equal(DispatchOutcome.Transient, result.Outcome);
        Assert.Equal("boom", result.Error);
        Assert.Same(exception, result.Exception);
    }

    [Fact]
    public void Permanent_WithException_ShouldCarryTheException()
    {
        var exception = new FormatException("bad address");

        var result = DispatchResult.Permanent("bad address", exception);

        Assert.Equal(DispatchOutcome.Permanent, result.Outcome);
        Assert.Equal("bad address", result.Error);
        Assert.Same(exception, result.Exception);
    }

    [Fact]
    public void Transient_WithoutException_ShouldLeaveExceptionNull()
    {
        var result = DispatchResult.Transient("sender reported failure");

        Assert.Null(result.Exception);
    }

    [Fact]
    public void Delivered_ShouldLeaveExceptionNull()
    {
        var result = DispatchResult.Delivered();

        Assert.Null(result.Exception);
    }
}
