using Themia.Data.Migrations;

using Xunit;

namespace Themia.Modules.Messaging.Tests;

public class MessagingModuleTests
{
    [Fact]
    public void Constructor_ShouldSucceed_WhenOriginIsSet()
    {
        var module = new MessagingModule(MigrationEngine.Postgres, new MessagingModuleOptions { Origin = "ezy-assets" });

        Assert.Equal("Themia.Messaging", module.Descriptor.Name);
    }

    // MessagingModuleOptions.Origin has no safe default (task 2 decision): a module built from
    // default-constructed options would carry a blank Origin, which Validate() must reject at
    // construction rather than let through to fail confusingly later during InitializeAsync.
    [Fact]
    public void Constructor_ShouldThrow_WhenOriginIsBlank()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => new MessagingModule(MigrationEngine.Postgres, new MessagingModuleOptions()));
        Assert.Equal("Origin", ex.ParamName);
    }
}
