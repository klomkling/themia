using Themia.Mediator.Pipelines;

namespace Themia.Mediator.Abstractions;

/// <summary>
/// Pipeline behavior for processing requests through the mediator pipeline.
/// </summary>
/// <typeparam name="TRequest">The type of request being handled.</typeparam>
/// <typeparam name="TResponse">The type of response returned.</typeparam>
public interface IPipelineBehavior<TRequest, TResponse>
    where TRequest : IRequest<TResponse>
{
    /// <summary>
    /// Handles the request as part of the pipeline.
    /// </summary>
    /// <param name="request">The request to handle.</param>
    /// <param name="next">The next step in the pipeline.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The response from the pipeline.</returns>
    Task<TResponse> HandleAsync(TRequest request,
        RequestHandlerContinuation<TResponse> next,
        CancellationToken cancellationToken = default);
}
