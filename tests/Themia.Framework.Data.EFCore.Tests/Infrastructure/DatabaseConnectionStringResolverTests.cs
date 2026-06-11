using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Framework.Data.EFCore.Infrastructure;
using Themia.MultiTenancy.Abstractions;
using Xunit;

namespace Themia.Framework.Data.EFCore.Tests.Infrastructure;

public sealed class DatabaseConnectionStringResolverTests
{
    private static IConfiguration ConfigWithDefault(string? value) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:Default"] = value })
            .Build();

    private sealed class StubAccessor(TenantInfo? current) : ITenantAccessor
    {
        public TenantInfo? Current { get; } = current;
    }

    private static IServiceProvider ProviderWith(ITenantAccessor? accessor)
    {
        var services = new ServiceCollection();
        if (accessor is not null)
        {
            services.AddSingleton(accessor);
        }
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Resolve_PrefersTenantConnectionString_WhenPresent()
    {
        var tenant = new TenantInfo("1", "acme", ConnectionString: "Host=tenant-db;Database=acme");
        var sp = ProviderWith(new StubAccessor(tenant));

        var result = DatabaseConnectionStringResolver.Resolve(ConfigWithDefault("Host=shared"), sp);

        Assert.Equal("Host=tenant-db;Database=acme", result);
    }

    [Fact]
    public void Resolve_FallsBackToDefault_WhenTenantHasNoConnectionString()
    {
        var tenant = new TenantInfo("1", "acme", ConnectionString: null);
        var sp = ProviderWith(new StubAccessor(tenant));

        var result = DatabaseConnectionStringResolver.Resolve(ConfigWithDefault("Host=shared"), sp);

        Assert.Equal("Host=shared", result);
    }

    [Fact]
    public void Resolve_FallsBackToDefault_WhenNoTenantAccessorRegistered()
    {
        var sp = ProviderWith(accessor: null);

        var result = DatabaseConnectionStringResolver.Resolve(ConfigWithDefault("Host=shared"), sp);

        Assert.Equal("Host=shared", result);
    }

    [Fact]
    public void Resolve_Throws_WhenNoTenantConnectionStringAndNoDefault()
    {
        var sp = ProviderWith(new StubAccessor(new TenantInfo("1", "acme", ConnectionString: null)));

        Assert.Throws<InvalidOperationException>(
            () => DatabaseConnectionStringResolver.Resolve(ConfigWithDefault(null), sp));
    }

    [Fact]
    public void Resolve_FallsBackToDefault_WhenTenantConnectionStringIsWhiteSpace()
    {
        var tenant = new TenantInfo("1", "acme", ConnectionString: "   ");
        var sp = ProviderWith(new StubAccessor(tenant));

        var result = DatabaseConnectionStringResolver.Resolve(ConfigWithDefault("Host=shared"), sp);

        Assert.Equal("Host=shared", result);
    }

    [Fact]
    public void Resolve_FallsBackToDefault_WhenTenantAccessorReturnsNullCurrent()
    {
        var sp = ProviderWith(new StubAccessor(current: null));

        var result = DatabaseConnectionStringResolver.Resolve(ConfigWithDefault("Host=shared"), sp);

        Assert.Equal("Host=shared", result);
    }
}
