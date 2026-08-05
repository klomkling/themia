using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Themia.Framework.Data.EFCore;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.EFCore;
using Themia.Modules.Identity.EFCore.DependencyInjection;
using Themia.Modules.Identity.EntityConfiguration;
using Xunit;

namespace Themia.Modules.Identity.Tests.DependencyInjection;

/// <summary>
/// The EF Core half of the engine split (coord #0058). The Dapper half can be checked at registration
/// time — the registry either exists or it does not — but nothing observable at registration time says
/// whether <c>ApplyThemiaIdentity</c> was called, so the guard is a startup check over the built model.
/// These run without a container; the integration suites are filtered out of the default CI leg.
/// </summary>
public class IdentityEFCoreRegistrationTests
{
    [Fact]
    public void AddThemiaIdentityEFCore_registers_the_core_services()
    {
        var services = new ServiceCollection();
        services.AddThemiaIdentityEFCore();

        var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetService<IPasswordHasher>());
        Assert.NotNull(provider.GetService<IdentityModuleOptions>());
    }

    [Fact]
    public void AddThemiaIdentityEFCore_passes_options_through()
    {
        var services = new ServiceCollection();
        services.AddThemiaIdentityEFCore(o => o.MaxFailedAccessAttempts = 9);

        var options = services.BuildServiceProvider().GetRequiredService<IdentityModuleOptions>();
        Assert.Equal(9, options.MaxFailedAccessAttempts);
    }

    [Fact]
    public void AddThemiaIdentityEFCore_registers_the_startup_model_check()
    {
        // Without this registration the EF leg keeps the failure the split exists to remove: a forgotten
        // ApplyThemiaIdentity starts cleanly and first surfaces as a query against a table EF never knew.
        var services = new ServiceCollection();
        services.AddThemiaIdentityEFCore();

        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService));
    }

    [Fact]
    public void Validate_throws_when_ApplyThemiaIdentity_was_not_called()
    {
        using var context = new BareContext(Options<BareContext>());

        var ex = Assert.Throws<InvalidOperationException>(() => IdentityModelValidation.Validate(context.Model));

        Assert.Contains("ApplyThemiaIdentity", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_throws_when_the_user_entity_is_mapped_to_another_table()
    {
        // The schema migration creates identity.users. A context that maps User anywhere else disagrees
        // with the schema its own module just applied, which is the same outage by another route.
        using var context = new WrongTableContext(Options<WrongTableContext>());

        var ex = Assert.Throws<InvalidOperationException>(() => IdentityModelValidation.Validate(context.Model));

        Assert.Contains("identity.users", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Validate_passes_for_a_context_that_applied_the_identity_model()
    {
        using var context = new IdentityContext(Options<IdentityContext>());

        IdentityModelValidation.Validate(context.Model);
    }

    // Model building needs a provider but no reachable server — nothing here opens a connection.
    private static DbContextOptions<TContext> Options<TContext>()
        where TContext : DbContext
        => new DbContextOptionsBuilder<TContext>().UseNpgsql("Host=localhost;Database=unused").Options;

    private sealed class BareContext(DbContextOptions<BareContext> options) : ThemiaDbContext(options);

    private sealed class IdentityContext(DbContextOptions<IdentityContext> options) : ThemiaDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.ApplyThemiaIdentity();
            base.OnModelCreating(modelBuilder);
        }
    }

    private sealed class WrongTableContext(DbContextOptions<WrongTableContext> options) : ThemiaDbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<Abstractions.Entities.User>().ToTable("users");
            base.OnModelCreating(modelBuilder);
        }
    }
}
