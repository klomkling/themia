using Microsoft.Extensions.DependencyInjection;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Auditing;
using Themia.Framework.Data.Dapper.Mapping;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Entities;
using Themia.Modules.Identity.DependencyInjection;
using Themia.Modules.Identity.Principal;
using Xunit;

namespace Themia.Modules.Identity.Tests.DependencyInjection;

public class IdentityServiceCollectionExtensionsTests
{
    [Fact]
    public void Registers_core_services_and_hasher()
    {
        var services = new ServiceCollection();
        services.AddThemiaIdentityCore();

        var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetService<IPasswordHasher>());
        Assert.NotNull(provider.GetService<IdentityModuleOptions>());
    }

    [Fact]
    public async Task Lifecycle_hooks_resolve_to_a_permissive_default()
    {
        var services = new ServiceCollection();
        services.AddThemiaIdentityCore();

        // UserService takes IUserLifecycleHooks as a required dependency, so a bare AddThemiaIdentityCore
        // must supply one — otherwise every adopter is forced to register a no-op before the module works.
        var hooks = services.BuildServiceProvider().GetRequiredService<IUserLifecycleHooks>();

        Assert.True((await hooks.OnBeforeDeleteAsync(Guid.CreateVersion7())).IsAllowed);
    }

    [Fact]
    public void An_adopters_lifecycle_hooks_survive_AddThemiaIdentityCore()
    {
        var services = new ServiceCollection();
        services.AddScoped<IUserLifecycleHooks, RefusingHooks>();
        services.AddThemiaIdentityCore();

        Assert.IsType<RefusingHooks>(services.BuildServiceProvider().GetRequiredService<IUserLifecycleHooks>());
    }

    private sealed class RefusingHooks : IUserLifecycleHooks
    {
        public ValueTask<UserMutationDecision> OnBeforeDeleteAsync(
            Guid userId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(UserMutationDecision.Refuse("no"));
    }

    [Fact]
    public void Authorization_replaces_the_null_current_user_accessor()
    {
        var services = new ServiceCollection();
        services.AddThemiaIdentityCore();
        services.AddThemiaIdentityAuthorization();

        var provider = services.BuildServiceProvider();
        Assert.IsType<IdentityCurrentUserAccessor>(provider.GetRequiredService<ICurrentUserAccessor>());
        Assert.NotNull(provider.GetService<ICurrentUser>());
    }

    [Fact]
    public void AddThemiaIdentityCore_contributes_no_dapper_mappings()
    {
        // The core no longer knows about Dapper. It used to scan the service collection for a registry and
        // contribute to whatever it found, which meant the Dapper path was INFERRED — and inferred wrong,
        // silently, whenever the peer registration ran second. AddThemiaIdentityDapper owns it now, and
        // this asserts the inference is gone rather than merely unused.
        var services = new ServiceCollection();
        var registry = new EntityMappingRegistry();
        services.AddSingleton(registry);
        services.AddThemiaIdentityCore();

        // Not "does For<User>() throw" — an unmapped type falls back to the registry's own convention
        // rather than failing. The invariant is that the IDENTITY mapping was not contributed.
        Assert.NotEqual("identity.users", registry.For<User>().Table);
    }

    [Fact]
    public void Options_are_configurable()
    {
        var services = new ServiceCollection();
        services.AddThemiaIdentityCore(o => o.MaxFailedAccessAttempts = 9);

        var options = services.BuildServiceProvider().GetRequiredService<IdentityModuleOptions>();
        Assert.Equal(9, options.MaxFailedAccessAttempts);
    }

    [Fact]
    public void AddThemiaIdentityCore_throws_for_invalid_options()
    {
        var services = new ServiceCollection();
        Assert.Throws<ArgumentOutOfRangeException>(
            () => services.AddThemiaIdentityCore(o => o.MaxFailedAccessAttempts = 0));
    }

    [Fact]
    public void AddThemiaIdentityCore_registers_supplied_options_instance()
    {
        var services = new ServiceCollection();
        var options = new IdentityModuleOptions { MaxFailedAccessAttempts = 7 };
        services.AddThemiaIdentityCore(options);

        var provider = services.BuildServiceProvider();
        Assert.Same(options, provider.GetRequiredService<IdentityModuleOptions>());
        Assert.NotNull(provider.GetService<IPasswordHasher>());
    }
}
