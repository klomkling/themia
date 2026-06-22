using Themia.Framework.Data.Dapper.Mapping;
using Themia.Modules.Notifications.Entities;
using Themia.Modules.Notifications.Mapping;
using Xunit;

namespace Themia.Modules.Notifications.Tests.Mapping;

public class NotificationsDapperMappingsTests
{
    [Fact]
    public void Apply_MapsOutboxStatusColumn()
    {
        var registry = new EntityMappingRegistry();
        NotificationsDapperMappings.Apply(registry);
        var mapping = registry.For<OutboxMessage>();
        Assert.Equal("status", mapping.Column(nameof(OutboxMessage.Status)));
        Assert.Equal("next_attempt_at", mapping.Column(nameof(OutboxMessage.NextAttemptAt)));
    }
}
