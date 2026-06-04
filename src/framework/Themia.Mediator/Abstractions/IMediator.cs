namespace Themia.Mediator.Abstractions;

/// <summary>
/// Mediator interface for sending requests to their handlers.
/// </summary>
public interface IMediator
{
    /// <summary>
    /// Sends a request to its handler and returns the response.
    /// </summary>
    /// <typeparam name="TResponse">The type of response expected from the request.</typeparam>
    /// <param name="request">The request to send.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The response from the handler.</returns>
    Task<TResponse> SendAsync<TResponse>(IRequest<TResponse> request, CancellationToken cancellationToken = default);
}
