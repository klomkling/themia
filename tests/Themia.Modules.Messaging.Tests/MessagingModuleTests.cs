using Themia.Data.Migrations;

using Xunit;

namespace Themia.Modules.Messaging.Tests;

public class MessagingModuleTests
{
    [Fact]
    public void Constructor_ShouldSucceed_WithValidOptions()
    {
        var module = new MessagingModule(MigrationEngine.Postgres, new MessagingModuleOptions());

        Assert.Equal("Themia.Messaging", module.Descriptor.Name);
    }

    // MessagingModuleOptions.ConnectionStringName is the last required string (task 2 decision: Origin
    // moved to MessagingIdentity, validated by its own constructor instead). A module built with a blank
    // ConnectionStringName must be rejected at construction rather than fail confusingly later during
    // InitializeAsync.
    [Fact]
    public void Constructor_ShouldThrow_WhenConnectionStringNameIsBlank()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => new MessagingModule(MigrationEngine.Postgres, new MessagingModuleOptions { ConnectionStringName = "" }));
        Assert.Equal("ConnectionStringName", ex.ParamName);
    }
}
