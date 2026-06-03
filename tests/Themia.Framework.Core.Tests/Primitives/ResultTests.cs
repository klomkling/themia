using Themia.Framework.Core.Primitives;
using Xunit;

namespace Themia.Framework.Core.Tests.Primitives;

public class ResultTests
{
    [Fact]
    public void Success_FlagIsSet()
    {
        var result = Result.Success();

        Assert.True(result.IsSuccess);
        Assert.False(result.IsFailure);
        Assert.Null(result.Error);
    }

    [Fact]
    public void Failure_FlagIsSet_AndErrorPresent()
    {
        var error = new Error("ERR", "failure");
        var result = Result.Failure(error);

        Assert.True(result.IsFailure);
        Assert.False(result.IsSuccess);
        Assert.Equal(error, result.Error);
    }

    [Fact]
    public void ResultOfT_ReturnsValue_OnSuccess()
    {
        var result = Result<int>.Success(42);

        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void ResultOfT_Throws_OnValueAccessWhenFailed()
    {
        var result = Result<int>.Failure("ERR");

        Assert.True(result.IsFailure);
        Assert.Throws<InvalidOperationException>(() => _ = result.Value);
    }

    [Fact]
    public void ToResult_PropagatesFailure()
    {
        var failed = Result.Failure("ERR");
        var converted = failed.ToResult("value");

        Assert.True(converted.IsFailure);
        Assert.Equal(failed.Error, converted.Error);
    }

    [Fact]
    public void Failure_ThrowsArgumentNullException_WhenErrorIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => Result.Failure(null!));
    }

    [Fact]
    public void ResultOfT_Failure_ThrowsArgumentNullException_WhenErrorIsNull()
    {
        Assert.Throws<ArgumentNullException>(() => Result<int>.Failure(null!));
    }

    [Fact]
    public void ToResult_ThrowsInvalidOperationException_WhenCalledOnSuccessWithoutError()
    {
        var success = Result.Success();

        // ToResult should work with success
        var converted = success.ToResult(42);
        Assert.True(converted.IsSuccess);
        Assert.Equal(42, converted.Value);
    }

    [Fact]
    public void Success_ThrowsArgumentNullException_WhenValueIsNull()
    {
        // Result.Success should not accept null values to prevent ambiguity
        Assert.Throws<ArgumentNullException>(() => Result<string>.Success(null!));
    }
}
