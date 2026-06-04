using FluentValidation;
using Themia.Mediator.Abstractions;
using Themia.Mediator.Behaviors;

namespace Themia.Mediator.Tests.Behaviors;

public sealed class ValidationBehaviorTests
{
    [Fact]
    public async Task Should_execute_async_validators()
    {
        var validator = new AsyncValidator();
        var behavior = new ValidationBehavior<TestRequest, string>(new[] { validator });

        var response = await behavior.HandleAsync(new TestRequest("pass"), _ => Task.FromResult("ok"), CancellationToken.None);

        Assert.Equal("ok", response);
        Assert.Equal(1, validator.AsyncInvocationCount);
    }

    [Fact]
    public async Task Should_throw_when_async_validator_fails()
    {
        var validator = new AsyncValidator();
        var behavior = new ValidationBehavior<TestRequest, string>(new[] { validator });

        await Assert.ThrowsAsync<ValidationException>(() =>
            behavior.HandleAsync(new TestRequest("fail"), _ => Task.FromResult("unused"), CancellationToken.None));
    }

    [Fact]
    public async Task Should_flow_cancellation_token_to_validators()
    {
        var validator = new CancellationAwareValidator();
        var behavior = new ValidationBehavior<TestRequest, string>(new[] { validator });
        using var cts = new CancellationTokenSource();

        var handleTask = behavior.HandleAsync(new TestRequest("any"), _ => Task.FromResult("ignored"), cts.Token);

        var capturedToken = await validator.CapturedToken.Task;
        Assert.Equal(cts.Token, capturedToken);

        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => handleTask);
    }
}

file sealed record TestRequest(string Value) : IRequest<string>;

file sealed class AsyncValidator : AbstractValidator<TestRequest>
{
    public int AsyncInvocationCount { get; private set; }

    public AsyncValidator()
    {
        RuleFor(x => x.Value)
            .MustAsync(async (value, cancellationToken) =>
            {
                await Task.Delay(10, cancellationToken);
                AsyncInvocationCount++;
                return value == "pass";
            });
    }
}

file sealed class CancellationAwareValidator : AbstractValidator<TestRequest>
{
    public TaskCompletionSource<CancellationToken> CapturedToken { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public CancellationAwareValidator()
    {
        RuleFor(x => x.Value)
            .MustAsync(async (_, cancellationToken) =>
            {
                CapturedToken.TrySetResult(cancellationToken);
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
                return true;
            });
    }
}
