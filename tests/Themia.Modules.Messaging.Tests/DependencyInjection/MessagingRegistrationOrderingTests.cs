using System.Data.Common;

using Microsoft.Extensions.DependencyInjection;

using Themia.Framework.Data.Dapper.Connection;
using Themia.Modules.Messaging.DependencyInjection;

using Xunit;

namespace Themia.Modules.Messaging.Tests.DependencyInjection;

// F6 + F7: both AddThemiaMessagingModule and AddThemiaMessagingInbox depend on services having been
// registered in a specific order by an earlier call. Getting the order wrong used to fail silently
// (F6: the Messaging entity mapping was never contributed, so the first enqueue failed later at commit)
// or with an opaque DI activation error at IHost.StartAsync (F7: InboxPurgeService couldn't resolve
// MessagingModuleOptions). Both must now fail loudly at registration time instead.
public class MessagingRegistrationOrderingTests
{
    // Minimal stand-in for a registered Dapper peer: enough for the "is a Dapper peer registered?"
    // detection in AddThemiaMessagingModule to see IDapperConnectionContext present, without pulling in
    // real Dapper connection/transaction plumbing.
    private sealed class StubDapperConnectionContext : IDapperConnectionContext
    {
        public DbTransaction? CurrentTransaction => null;
        public Task<DbConnection> GetOpenConnectionAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<DbTransaction> BeginTransactionAsync(CancellationToken cancellationToken) => throw new NotSupportedException();
        public ValueTask DisposeTransactionAsync() => ValueTask.CompletedTask;
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    [Fact]
    public void AddThemiaMessagingModule_ShouldThrow_WhenDapperPeerRegistered_ButEntityMappingRegistryIsNot()
    {
        var services = new ServiceCollection();
        services.AddScoped<IDapperConnectionContext, StubDapperConnectionContext>();

        var ex = Assert.Throws<InvalidOperationException>(
            () => services.AddThemiaMessagingModule(o => o.Origin = "test-origin"));

        Assert.Contains("AddThemiaDapperCore", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddThemiaMessagingModule_ShouldNotThrow_OnTheEFOnlyPath()
    {
        var services = new ServiceCollection();

        var exception = Record.Exception(() => services.AddThemiaMessagingModule(o => o.Origin = "test-origin"));

        Assert.Null(exception);
    }

    [Fact]
    public void AddThemiaMessagingInbox_ShouldThrow_WhenAddThemiaMessagingModuleWasNotCalled()
    {
        var services = new ServiceCollection();
        // Satisfy the Dapper-peer guard so the module-registration guard is the one under test.
        services.AddScoped<IDapperConnectionContext, StubDapperConnectionContext>();

        var ex = Assert.Throws<InvalidOperationException>(() => services.AddThemiaMessagingInbox());

        Assert.Contains("AddThemiaMessagingModule", ex.Message, StringComparison.Ordinal);
    }
}
