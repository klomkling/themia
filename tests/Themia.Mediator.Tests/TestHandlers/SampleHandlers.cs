using System;
using System.Threading;
using System.Threading.Tasks;
using Themia.DependencyInjection;
using Themia.Mediator.Abstractions;

[assembly: Themia.Mediator.GenerateMediatorHandlers]

namespace Themia.Mediator.Tests.TestHandlers;

// Commands
public record CreateOrderCommand(string ProductName, int Quantity) : ICommand<Guid>;

public record DeleteOrderCommand(Guid OrderId) : ICommand<bool>;

// Queries
public record GetOrderQuery(Guid OrderId) : IQuery<OrderDto>;

public record ListOrdersQuery : IQuery<OrderDto[]>;

// Request (neither Command nor Query)
public record PingRequest : IRequest<string>;

// Response DTOs
public record OrderDto(Guid Id, string ProductName, int Quantity, DateTime CreatedAt);

// Handlers with default lifetime (Scoped)
public class CreateOrderHandler : ICommandHandler<CreateOrderCommand, Guid>
{
    public Task<Guid> HandleAsync(CreateOrderCommand request, CancellationToken cancellationToken)
    {
        var orderId = Guid.NewGuid();
        return Task.FromResult(orderId);
    }
}

public class GetOrderHandler : IQueryHandler<GetOrderQuery, OrderDto>
{
    public Task<OrderDto> HandleAsync(GetOrderQuery request, CancellationToken cancellationToken)
    {
        var order = new OrderDto(request.OrderId, "Sample Product", 5, DateTime.UtcNow);
        return Task.FromResult(order);
    }
}

// Handler with custom lifetime (Transient)
// AllowSelfRegistration=true: the DI generator would otherwise error on no matching IDeleteOrderHandler interface.
// The mediator generator reads the [Transient] attr to set the handler's DI lifetime.
[Transient(AllowSelfRegistration = true)]
public class DeleteOrderHandler : ICommandHandler<DeleteOrderCommand, bool>
{
    public Task<bool> HandleAsync(DeleteOrderCommand request, CancellationToken cancellationToken)
    {
        return Task.FromResult(true);
    }
}

// Handler with custom lifetime (Singleton)
// AllowSelfRegistration=true: the DI generator would otherwise error on no matching IListOrdersHandler interface.
// The mediator generator reads the [Singleton] attr to set the handler's DI lifetime.
[Singleton(AllowSelfRegistration = true)]
public class ListOrdersHandler : IQueryHandler<ListOrdersQuery, OrderDto[]>
{
    public Task<OrderDto[]> HandleAsync(ListOrdersQuery request, CancellationToken cancellationToken)
    {
        var orders = new[]
        {
            new OrderDto(Guid.NewGuid(), "Product 1", 1, DateTime.UtcNow),
            new OrderDto(Guid.NewGuid(), "Product 2", 2, DateTime.UtcNow)
        };
        return Task.FromResult(orders);
    }
}

// Generic request handler
public class PingHandler : IRequestHandler<PingRequest, string>
{
    public Task<string> HandleAsync(PingRequest request, CancellationToken cancellationToken)
    {
        return Task.FromResult("Pong!");
    }
}
