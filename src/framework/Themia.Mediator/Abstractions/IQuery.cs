namespace Themia.Mediator.Abstractions;

/// <summary>
/// Marker interface for queries that return a response.
/// </summary>
/// <typeparam name="TResponse">The type of response returned by the query.</typeparam>
public interface IQuery<TResponse> : IRequest<TResponse>
{
}

/// <summary>
/// Handler interface for processing queries.
/// </summary>
/// <typeparam name="TQuery">The type of query to handle.</typeparam>
/// <typeparam name="TResponse">The type of response returned by the query.</typeparam>
public interface IQueryHandler<TQuery, TResponse> : IRequestHandler<TQuery, TResponse>
    where TQuery : IQuery<TResponse>
{
}
