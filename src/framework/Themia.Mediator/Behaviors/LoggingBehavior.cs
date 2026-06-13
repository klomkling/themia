using Microsoft.Extensions.Logging;
using Themia.Logging;
using Themia.Mediator.Abstractions;
using Themia.Mediator.Pipelines;

namespace Themia.Mediator.Behaviors;

/// <summary>
/// Pipeline behavior that logs request handling.
/// </summary>
/// <typeparam name="TRequest">The type of request being handled.</typeparam>
/// <typeparam name="TResponse">The type of response returned.</typeparam>
public sealed class LoggingBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    private readonly ILogger<LoggingBehavior<TRequest, TResponse>> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="LoggingBehavior{TRequest, TResponse}"/> class.
    /// </summary>
    /// <param name="logger">The logger instance.</param>
    public LoggingBehavior(ILogger<LoggingBehavior<TRequest, TResponse>> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Handles the request and logs before and after execution.
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

        // Push request name into log context for all logs within this scope
        using var _ = ThemiaLogContext.PushProperty("RequestName", requestName);
        using var __ = ThemiaLogContext.PushProperty("RequestType", typeof(TRequest).FullName);

        _logger.LogInformation("Handling {RequestName}", requestName);

        try
        {
            var response = await next(cancellationToken).ConfigureAwait(false);

            _logger.LogInformation("Handled {RequestName}", requestName);

            return response;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("{RequestName} was cancelled", requestName);
            throw;
        }
#pragma warning disable THEMIA101 // Deliberate: LoggingBehavior IS the pipeline-level error logger; logging before rethrow is its purpose.
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error handling {RequestName}: {ErrorMessage}", requestName, ex.Message);
            throw;
        }
#pragma warning restore THEMIA101
    }
}
