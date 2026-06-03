using Microsoft.Extensions.DependencyInjection;
using Xunit;
using Themia.Framework.Core.Abstractions.Events;
using Themia.Framework.Core.Events;

namespace Themia.Framework.Core.Tests.Events;

public class DomainEventDispatcherTests
{
    [Fact]
    public async Task DispatchAsync_SingleEvent_CallsRegisteredHandler()
    {
        var services = new ServiceCollection();
        var handler = new TestEventHandler();
        services.AddSingleton<IDomainEventHandler<TestEvent>>(handler);

        var serviceProvider = services.BuildServiceProvider();
        var dispatcher = new DomainEventDispatcher(serviceProvider);

        var @event = new TestEvent("test-data");
        await dispatcher.DispatchAsync(@event);

        Assert.True(handler.WasCalled);
        Assert.Equal("test-data", handler.ReceivedEvent?.Data);
    }

    [Fact]
    public async Task DispatchAsync_MultipleHandlers_CallsAllHandlers()
    {
        var services = new ServiceCollection();
        var handler1 = new TestEventHandler();
        var handler2 = new TestEventHandler();
        services.AddSingleton<IDomainEventHandler<TestEvent>>(handler1);
        services.AddSingleton<IDomainEventHandler<TestEvent>>(handler2);

        var serviceProvider = services.BuildServiceProvider();
        var dispatcher = new DomainEventDispatcher(serviceProvider);

        var @event = new TestEvent("test-data");
        await dispatcher.DispatchAsync(@event);

        Assert.True(handler1.WasCalled);
        Assert.True(handler2.WasCalled);
    }

    [Fact]
    public async Task DispatchAsync_MultipleEvents_CallsHandlersForEach()
    {
        var services = new ServiceCollection();
        var handler = new TestEventHandler();
        services.AddSingleton<IDomainEventHandler<TestEvent>>(handler);

        var serviceProvider = services.BuildServiceProvider();
        var dispatcher = new DomainEventDispatcher(serviceProvider);

        var events = new List<IDomainEvent>
        {
            new TestEvent("event-1"),
            new TestEvent("event-2")
        };

        await dispatcher.DispatchAsync(events);

        Assert.Equal(2, handler.CallCount);
    }

    [Fact]
    public async Task DispatchAsync_MultipleHandlers_InvokesSequentially()
    {
        // Handlers share state: sequential dispatch must never overlap, so the second handler
        // always observes the first as already finished.
        var services = new ServiceCollection();
        var order = new List<string>();
        services.AddSingleton<IDomainEventHandler<TestEvent>>(new OrderingHandler("first", order, delayMs: 30));
        services.AddSingleton<IDomainEventHandler<TestEvent>>(new OrderingHandler("second", order, delayMs: 0));

        var serviceProvider = services.BuildServiceProvider();
        var dispatcher = new DomainEventDispatcher(serviceProvider);

        await dispatcher.DispatchAsync(new TestEvent("test-data"));

        Assert.Equal(new[] { "first:start", "first:end", "second:start", "second:end" }, order);
    }

    [Fact]
    public async Task DispatchAsync_NoHandlers_DoesNotThrow()
    {
        var services = new ServiceCollection();
        var serviceProvider = services.BuildServiceProvider();
        var dispatcher = new DomainEventDispatcher(serviceProvider);

        var @event = new TestEvent("test-data");
        await dispatcher.DispatchAsync(@event);

        // Should complete without throwing
        Assert.True(true);
    }

    [Fact]
    public void Constructor_NullServiceProvider_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => new DomainEventDispatcher(null!));
    }

    private sealed record TestEvent(string Data) : DomainEventBase;

    private sealed class OrderingHandler(string name, List<string> order, int delayMs)
        : IDomainEventHandler<TestEvent>
    {
        public async Task HandleAsync(TestEvent domainEvent, CancellationToken cancellationToken = default)
        {
            order.Add($"{name}:start");
            if (delayMs > 0)
            {
                await Task.Delay(delayMs, cancellationToken);
            }

            order.Add($"{name}:end");
        }
    }

    private sealed class TestEventHandler : IDomainEventHandler<TestEvent>
    {
        public bool WasCalled { get; private set; }
        public TestEvent? ReceivedEvent { get; private set; }
        public int CallCount { get; private set; }

        public Task HandleAsync(TestEvent domainEvent, CancellationToken cancellationToken = default)
        {
            WasCalled = true;
            ReceivedEvent = domainEvent;
            CallCount++;
            return Task.CompletedTask;
        }
    }
}
