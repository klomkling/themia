using Themia.Framework.Core.Extensions;
using Themia.Framework.Core.Primitives;
using Xunit;

namespace Themia.Framework.Core.Tests.Extensions;

public class ResultExtensionsTests
{
    [Fact]
    public void Map_TransformsValue_OnSuccess()
    {
        var result = Result<int>.Success(21).Map(x => x * 2);

        Assert.True(result.IsSuccess);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void Map_PropagatesError_WithoutInvokingSelector()
    {
        var selectorInvoked = false;
        var result = Result<int>.Failure("E1").Map(x =>
        {
            selectorInvoked = true;
            return x * 2;
        });

        Assert.True(result.IsFailure);
        Assert.Equal("E1", result.Error!.Code);
        Assert.False(selectorInvoked);
    }

    [Fact]
    public void Bind_ChainsResult_OnSuccess()
    {
        var result = Result<int>.Success(10).Bind(x => Result<string>.Success($"v{x}"));

        Assert.True(result.IsSuccess);
        Assert.Equal("v10", result.Value);
    }

    [Fact]
    public void Bind_PropagatesError_WithoutInvokingBinder()
    {
        var binderInvoked = false;
        var result = Result<int>.Failure("E1").Bind(x =>
        {
            binderInvoked = true;
            return Result<string>.Success($"v{x}");
        });

        Assert.True(result.IsFailure);
        Assert.Equal("E1", result.Error!.Code);
        Assert.False(binderInvoked);
    }

    [Fact]
    public void Combine_ReturnsAllValues_WhenAllSucceed()
    {
        var result = ResultExtensions.Combine(
            Result<int>.Success(1),
            Result<int>.Success(2),
            Result<int>.Success(3));

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { 1, 2, 3 }, result.Value);
    }

    [Fact]
    public void Combine_ReturnsFirstFailure_AndShortCircuits()
    {
        // Fail-fast contract: the first failure wins, distinguishing Combine from CombineAll.
        var result = ResultExtensions.Combine(
            Result<int>.Success(1),
            Result<int>.Failure("E1"),
            Result<int>.Failure("E2"));

        Assert.True(result.IsFailure);
        Assert.Equal("E1", result.Error!.Code);
    }

    [Fact]
    public void CombineAll_AggregatesAllErrorCodes()
    {
        var result = ResultExtensions.CombineAll(
            Result<int>.Success(1),
            Result<int>.Failure("E1"),
            Result<int>.Failure("E2"));

        Assert.True(result.IsFailure);
        Assert.Equal("MULTIPLE_ERRORS", result.Error!.Code);
        Assert.Contains("E1", result.Error.Message);
        Assert.Contains("E2", result.Error.Message);
    }

    [Fact]
    public void Sequence_ShortCircuits_OnFirstFailure()
    {
        var results = new[]
        {
            Result<int>.Success(1),
            Result<int>.Failure("E1"),
            Result<int>.Success(3),
        };

        var sequenced = results.Sequence();

        Assert.True(sequenced.IsFailure);
        Assert.Equal("E1", sequenced.Error!.Code);
    }

    [Fact]
    public void Traverse_AppliesSelectorAndSequences()
    {
        var success = new[] { 1, 2, 3 }.Traverse(x => Result<int>.Success(x * 10));
        Assert.True(success.IsSuccess);
        Assert.Equal(new[] { 10, 20, 30 }, success.Value);

        var failure = new[] { 1, -1, 3 }.Traverse(x =>
            x < 0 ? Result<int>.Failure("NEG") : Result<int>.Success(x));
        Assert.True(failure.IsFailure);
        Assert.Equal("NEG", failure.Error!.Code);
    }

    [Fact]
    public void Partition_SplitsSuccessesAndFailures()
    {
        var results = new[]
        {
            Result<int>.Success(1),
            Result<int>.Failure("E1"),
            Result<int>.Success(2),
        };

        var (successes, failures) = results.Partition();

        Assert.Equal(new[] { 1, 2 }, successes);
        Assert.Single(failures);
        Assert.Equal("E1", failures[0].Code);
    }

    [Fact]
    public void ValueOr_ReturnsFallback_OnFailure()
    {
        Assert.Equal(99, Result<int>.Failure("E1").ValueOr(99));
        Assert.Equal(5, Result<int>.Success(5).ValueOr(99));
    }

    [Fact]
    public void ValueOr_FactoryOverload_ReceivesError_OnFailure_AndSkipsOnSuccess()
    {
        var fromError = Result<int>.Failure("E1").ValueOr(err => err.Code.Length);
        Assert.Equal("E1".Length, fromError);

        var factoryInvoked = false;
        var onSuccess = Result<int>.Success(7).ValueOr(_ =>
        {
            factoryInvoked = true;
            return 0;
        });
        Assert.Equal(7, onSuccess);
        Assert.False(factoryInvoked);
    }

    [Fact]
    public void OnSuccess_And_OnFailure_InvokeOnlyMatchingBranch()
    {
        var successSeen = 0;
        var failureSeen = 0;

        Result<int>.Success(3)
            .OnSuccess(_ => successSeen++)
            .OnFailure(_ => failureSeen++);

        Result<int>.Failure("E1")
            .OnSuccess(_ => successSeen++)
            .OnFailure(_ => failureSeen++);

        Assert.Equal(1, successSeen);
        Assert.Equal(1, failureSeen);
    }
}
