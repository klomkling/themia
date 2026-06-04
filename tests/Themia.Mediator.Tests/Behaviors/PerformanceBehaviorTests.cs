using Microsoft.Extensions.Logging;
using Themia.Mediator.Abstractions;
using Themia.Mediator.Behaviors;
using Themia.Mediator.Pipelines;
using Themia.Mediator.Tests.TestDoubles;

namespace Themia.Mediator.Tests.Behaviors;

public sealed class PerformanceBehaviorTests
{
    // PerformanceBehavior exposes a two-arg constructor that accepts a custom TimeSpan threshold,
    // which allows tests to inject a very small value without any options infrastructure.

    [Fact]
    public async Task HandleAsync_logs_warning_when_handler_exceeds_threshold()
    {
        // Arrange
        var logger = new RecordingTestLogger<PerformanceBehavior<SlowRequest, string>>();
        var behavior = new PerformanceBehavior<SlowRequest, string>(logger, TimeSpan.FromMilliseconds(1));

        RequestHandlerContinuation<string> next = async ct =>
        {
            await Task.Delay(50, ct);
            return "done";
        };

        // Act
        var result = await behavior.HandleAsync(new SlowRequest(), next, CancellationToken.None);

        // Assert
        Assert.Equal("done", result);
        Assert.Contains(logger.Entries, e =>
            e.Level == LogLevel.Warning &&
            e.Message.Contains("Long Running Request"));
    }

    [Fact]
    public async Task HandleAsync_does_not_log_warning_when_handler_is_fast()
    {
        // Arrange — use a very large threshold so the handler is always "fast"
        var logger = new RecordingTestLogger<PerformanceBehavior<FastRequest, string>>();
        var behavior = new PerformanceBehavior<FastRequest, string>(logger, TimeSpan.FromHours(1));

        RequestHandlerContinuation<string> next = _ => Task.FromResult("quick");

        // Act
        var result = await behavior.HandleAsync(new FastRequest(), next, CancellationToken.None);

        // Assert
        Assert.Equal("quick", result);
        Assert.DoesNotContain(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    // Minimal request types for these tests
    private sealed record SlowRequest : IRequest<string>;
    private sealed record FastRequest : IRequest<string>;
}
