using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Themia.Logging;
using Themia.Mediator.Abstractions;
using Themia.Mediator.Pipelines;

namespace Themia.Mediator.Behaviors;

/// <summary>
/// Pipeline behavior that measures and logs long-running requests.
/// </summary>
/// <typeparam name="TRequest">The type of request being handled.</typeparam>
/// <typeparam name="TResponse">The type of response returned.</typeparam>
public sealed class PerformanceBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly ILogger<PerformanceBehavior<TRequest, TResponse>> _logger;
    private readonly TimeSpan _threshold;

    /// <summary>
    /// Initializes a new instance of the <see cref="PerformanceBehavior{TRequest, TResponse}"/> class with a default threshold of 500ms.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    public PerformanceBehavior(ILogger<PerformanceBehavior<TRequest, TResponse>> logger)
        : this(logger, TimeSpan.FromMilliseconds(500))
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="PerformanceBehavior{TRequest, TResponse}"/> class with a custom threshold.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    /// <param name="threshold">The threshold for logging long-running requests.</param>
    public PerformanceBehavior(ILogger<PerformanceBehavior<TRequest, TResponse>> logger, TimeSpan threshold)
    {
        _logger = logger;
        _threshold = threshold;
    }

    /// <summary>
    /// Handles the request and logs if execution time exceeds the threshold.
    /// </summary>
    /// <param name="request">The request to handle.</param>
    /// <param name="next">The next step in the pipeline.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The response from the handler.</returns>
    public async Task<TResponse> HandleAsync(
        TRequest request,
        RequestHandlerContinuation<TResponse> next,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(next);

        var requestName = typeof(TRequest).Name;

        // Push request name into log context
        using var _ = ThemiaLogContext.PushProperty("RequestName", requestName);

        var timer = Stopwatch.StartNew();

        var response = await next(cancellationToken).ConfigureAwait(false);

        timer.Stop();

        var elapsedMilliseconds = timer.ElapsedMilliseconds;

        if (elapsedMilliseconds <= _threshold.TotalMilliseconds) return response;

        _logger.LogWarning(
            "Long Running Request: {Name} ({ElapsedMilliseconds} milliseconds) {@Request}",
            requestName,
            elapsedMilliseconds,
            request);

        return response;
    }
}
