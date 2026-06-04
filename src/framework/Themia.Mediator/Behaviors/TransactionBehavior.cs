using Themia.Mediator.Abstractions;
using Themia.Mediator.Pipelines;

namespace Themia.Mediator.Behaviors;

/// <summary>
/// Placeholder behavior for wrapping requests in transactional boundaries. Infrastructure projects can replace this with a real implementation.
/// </summary>
/// <typeparam name="TRequest">The type of request being handled.</typeparam>
/// <typeparam name="TResponse">The type of response returned.</typeparam>
public sealed class TransactionBehavior<TRequest, TResponse> : IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    /// <summary>
    /// Handles the request within a transactional context (placeholder implementation).
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

        return await next(cancellationToken).ConfigureAwait(false);
    }
}
