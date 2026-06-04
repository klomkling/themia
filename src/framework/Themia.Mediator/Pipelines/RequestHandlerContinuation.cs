namespace Themia.Mediator.Pipelines;

/// <summary>
/// Represents the continuation delegate for the next step in the pipeline.
/// </summary>
/// <typeparam name="TResponse">The type of response returned.</typeparam>
/// <param name="cancellationToken">Cancellation token.</param>
/// <returns>A task representing the asynchronous operation.</returns>
public delegate Task<TResponse> RequestHandlerContinuation<TResponse>(CancellationToken cancellationToken = default);
