using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Themia.Mediator.Abstractions;
using Themia.Mediator.Extensions;
using Themia.Mediator.Generated;
using Themia.Mediator.Tests.TestHandlers;

namespace Themia.Mediator.Tests;

public class MediatorIntegrationTests : IDisposable
{
    private readonly ServiceProvider _serviceProvider;
    private readonly IMediator _mediator;

    public MediatorIntegrationTests()
    {
        var services = new ServiceCollection();

        // Register logging (required by behaviors)
        services.AddLogging();

        // Register mediator infrastructure (behaviors)
        services.AddApplicationMediator();

        // Register handlers and the dispatcher via the source-generated extension.
        // The [assembly: GenerateMediatorHandlers] attribute in TestHandlers/SampleHandlers.cs
        // causes the generator to emit AddGeneratedMediatorHandlers() for this assembly.
        services.AddGeneratedMediatorHandlers();

        _serviceProvider = services.BuildServiceProvider();
        _mediator = _serviceProvider.GetRequiredService<IMediator>();
    }

    [Fact]
    public async Task SendAsync_WithCommandHandler_ReturnsExpectedResult()
    {
        // Arrange
        var command = new CreateOrderCommand("Test Product", 10);

        // Act
        var result = await _mediator.SendAsync(command);

        // Assert
        Assert.NotEqual(Guid.Empty, result);
    }

    [Fact]
    public async Task SendAsync_WithQueryHandler_ReturnsExpectedResult()
    {
        // Arrange
        var orderId = Guid.NewGuid();
        var query = new GetOrderQuery(orderId);

        // Act
        var result = await _mediator.SendAsync(query);

        // Assert
        Assert.NotNull(result);
        Assert.Equal(orderId, result.Id);
        Assert.Equal("Sample Product", result.ProductName);
        Assert.Equal(5, result.Quantity);
    }

    [Fact]
    public async Task SendAsync_WithRequestHandler_ReturnsExpectedResult()
    {
        // Arrange
        var request = new PingRequest();

        // Act
        var result = await _mediator.SendAsync(request);

        // Assert
        Assert.Equal("Pong!", result);
    }

    [Fact]
    public async Task SendAsync_WithMultipleRequests_RoutesToCorrectHandlers()
    {
        // Arrange
        var createCommand = new CreateOrderCommand("Product A", 5);
        var deleteCommand = new DeleteOrderCommand(Guid.NewGuid());
        var listQuery = new ListOrdersQuery();

        // Act
        var createResult = await _mediator.SendAsync(createCommand);
        var deleteResult = await _mediator.SendAsync(deleteCommand);
        var listResult = await _mediator.SendAsync(listQuery);

        // Assert
        Assert.NotEqual(Guid.Empty, createResult);
        Assert.True(deleteResult);
        Assert.NotNull(listResult);
        Assert.Equal(2, listResult.Length);
    }

    [Fact]
    public async Task SendAsync_WithCancellationToken_PassesToHandler()
    {
        // Arrange
        var command = new CreateOrderCommand("Test", 1);
        using var cts = new CancellationTokenSource();

        // Act
        var result = await _mediator.SendAsync(command, cts.Token);

        // Assert
        Assert.NotEqual(Guid.Empty, result);
    }

    [Fact]
    public async Task SendAsync_WithUnregisteredHandler_ThrowsException()
    {
        // Arrange
        var unregisteredRequest = new UnregisteredRequest();

        // Act & Assert
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await _mediator.SendAsync(unregisteredRequest));

        Assert.Contains("No handler registered", exception.Message);
    }

    [Fact]
    public void Mediator_IsRegisteredAsScoped()
    {
        // Arrange & Act
        var mediator1 = _serviceProvider.GetRequiredService<IMediator>();
        var mediator2 = _serviceProvider.GetRequiredService<IMediator>();

        // Assert — scoped: same instance within the same (root) scope
        Assert.Same(mediator1, mediator2);
    }

    [Fact]
    public void Handlers_AreRegisteredWithCorrectLifetime()
    {
        // Arrange & Act
        var createHandler1 = _serviceProvider.GetRequiredService<ICommandHandler<CreateOrderCommand, Guid>>();
        var createHandler2 = _serviceProvider.GetRequiredService<ICommandHandler<CreateOrderCommand, Guid>>();

        var listHandler1 = _serviceProvider.GetRequiredService<IQueryHandler<ListOrdersQuery, OrderDto[]>>();
        var listHandler2 = _serviceProvider.GetRequiredService<IQueryHandler<ListOrdersQuery, OrderDto[]>>();

        // Assert - CreateOrderHandler is Scoped (default): same instance in same scope
        Assert.Same(createHandler1, createHandler2);

        // Assert - ListOrdersHandler is Singleton: always same instance
        Assert.Same(listHandler1, listHandler2);
    }

    [Fact]
    public void TransientHandlers_CreateNewInstanceEachTime()
    {
        // Arrange & Act
        var handler1 = _serviceProvider.GetRequiredService<ICommandHandler<DeleteOrderCommand, bool>>();
        var handler2 = _serviceProvider.GetRequiredService<ICommandHandler<DeleteOrderCommand, bool>>();

        // Assert
        Assert.NotSame(handler1, handler2);
    }

    [Fact]
    public async Task SendAsync_WithNullRequest_ThrowsArgumentNullException()
    {
        // Act & Assert
        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await _mediator.SendAsync<string>(null!));
    }

    public void Dispose()
    {
        _serviceProvider.Dispose();
    }

    // Helper class for testing unregistered request
    private record UnregisteredRequest : IRequest<string>;
}
