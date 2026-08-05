using Microsoft.Extensions.DependencyInjection;
using Themia.Framework.Data.Dapper.Mapping;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.Dapper.DependencyInjection;
using Xunit;

namespace Themia.Modules.Identity.Tests.DependencyInjection;

/// <summary>
/// The Dapper half of the engine split (coord #0058): the mapping contribution that used to happen
/// implicitly inside the core registration now has its own named entry point.
/// </summary>
public class IdentityDapperRegistrationTests
{
    [Fact]
    public void AddThemiaIdentityDapper_registers_the_core_services_and_the_mappings()
    {
        var services = new ServiceCollection();
        var registry = new EntityMappingRegistry();
        services.AddSingleton(registry);                 // the Dapper peer registration, run first
        services.AddThemiaIdentityDapper();

        Assert.Equal("identity.users", registry.For<User>().Table);
        Assert.NotNull(services.BuildServiceProvider().GetService<IdentityModuleOptions>());
    }

    [Fact]
    public void AddThemiaIdentityDapper_throws_when_the_peer_has_not_been_registered()
    {
        // This is the whole reason the entry point exists. The old scan returned quietly when it found no
        // registry, so calling the registrations in the wrong order left identity mappings unapplied with
        // nothing to observe until a query came back with the wrong columns.
        var services = new ServiceCollection();

        var ex = Assert.Throws<InvalidOperationException>(() => services.AddThemiaIdentityDapper());

        Assert.Contains(nameof(IdentityDapperServiceCollectionExtensions.AddThemiaIdentityDapper), ex.Message, StringComparison.Ordinal);
        Assert.Contains("Register the Dapper data peer FIRST", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AddThemiaIdentityDapper_passes_options_through()
    {
        var services = new ServiceCollection();
        services.AddSingleton(new EntityMappingRegistry());
        services.AddThemiaIdentityDapper(o => o.MaxFailedAccessAttempts = 9);

        var options = services.BuildServiceProvider().GetRequiredService<IdentityModuleOptions>();
        Assert.Equal(9, options.MaxFailedAccessAttempts);
    }
}
