using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.EFCore;
using Themia.Framework.Data.EFCore.PostgreSql;
using Themia.MultiTenancy.Abstractions;
using Xunit;

namespace Themia.Framework.Data.EFCore.Tests.Providers;

public sealed class TenantConnectionRoutingTests
{
    // ThemiaDbContext ctor: (DbContextOptions options, ITenantContext? tenantContext = null, TimeProvider? timeProvider = null)
    // The generic DbContextOptions<T> is assignable to DbContextOptions, so the primary-constructor
    // form with DbContextOptions<RoutingDbContext> compiles fine against the base that accepts DbContextOptions.
    private sealed class RoutingDbContext(DbContextOptions<RoutingDbContext> options, ITenantContext? tenantContext = null)
        : ThemiaDbContext(options, tenantContext);

    private sealed class MutableAccessor : ITenantAccessor
    {
        public TenantInfo? Current { get; set; }
    }

    private static ServiceProvider BuildProvider(MutableAccessor accessor)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = "Host=shared-db;Database=shared",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<ITenantAccessor>(accessor);
        services.AddThemiaPostgres<RoutingDbContext>(configuration);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void DbContext_UsesTenantConnectionString_WhenTenantResolved()
    {
        var accessor = new MutableAccessor
        {
            Current = new TenantInfo("1", "acme", ConnectionString: "Host=acme-db;Database=acme"),
        };
        using var sp = BuildProvider(accessor);
        using var scope = sp.CreateScope();

        var context = scope.ServiceProvider.GetRequiredService<RoutingDbContext>();

        Assert.Contains("acme-db", context.Database.GetConnectionString());
    }

    [Fact]
    public void DbContext_UsesDefaultConnectionString_WhenNoTenantResolved()
    {
        var accessor = new MutableAccessor { Current = null };
        using var sp = BuildProvider(accessor);
        using var scope = sp.CreateScope();

        var context = scope.ServiceProvider.GetRequiredService<RoutingDbContext>();

        Assert.Contains("shared-db", context.Database.GetConnectionString());
    }

    [Fact]
    public void DbContext_UsesDefaultConnectionString_WhenTenantConnectionStringIsWhiteSpace()
    {
        var accessor = new MutableAccessor
        {
            Current = new TenantInfo("1", "acme", ConnectionString: "   "),
        };
        using var sp = BuildProvider(accessor);
        using var scope = sp.CreateScope();

        var context = scope.ServiceProvider.GetRequiredService<RoutingDbContext>();

        Assert.Contains("shared-db", context.Database.GetConnectionString());
    }

}
