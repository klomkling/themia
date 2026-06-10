using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Framework.Data.Dapper.Connection;
using Themia.MultiTenancy.Abstractions;
using Xunit;

namespace Themia.Framework.Data.Dapper.Tests;

/// <summary>
/// Unit tests for the shared <see cref="DapperConnectionString"/> resolver used by every engine factory:
/// the ambient tenant connection string wins when present, otherwise the "Default" one, otherwise it throws.
/// </summary>
public sealed class DapperConnectionStringTests
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
        var tenant = new TenantInfo("1", "acme", ConnectionString: "Server=tenant-db;Database=acme");
        var sp = ProviderWith(new StubAccessor(tenant));

        Assert.Equal("Server=tenant-db;Database=acme", DapperConnectionString.Resolve(ConfigWithDefault("Server=shared"), sp));
    }

    [Fact]
    public void Resolve_FallsBackToDefault_WhenNoTenantAccessorRegistered()
    {
        var sp = ProviderWith(accessor: null);

        Assert.Equal("Server=shared", DapperConnectionString.Resolve(ConfigWithDefault("Server=shared"), sp));
    }

    [Fact]
    public void Resolve_FallsBackToDefault_WhenTenantHasNoConnectionString()
    {
        var sp = ProviderWith(new StubAccessor(new TenantInfo("1", "acme", ConnectionString: null)));

        Assert.Equal("Server=shared", DapperConnectionString.Resolve(ConfigWithDefault("Server=shared"), sp));
    }

    [Fact]
    public void Resolve_FallsBackToDefault_WhenTenantConnectionStringIsWhiteSpace()
    {
        var sp = ProviderWith(new StubAccessor(new TenantInfo("1", "acme", ConnectionString: "   ")));

        Assert.Equal("Server=shared", DapperConnectionString.Resolve(ConfigWithDefault("Server=shared"), sp));
    }

    [Fact]
    public void Resolve_FallsBackToDefault_WhenTenantAccessorReturnsNullCurrent()
    {
        var sp = ProviderWith(new StubAccessor(current: null));

        Assert.Equal("Server=shared", DapperConnectionString.Resolve(ConfigWithDefault("Server=shared"), sp));
    }

    [Fact]
    public void Resolve_Throws_WhenNoTenantConnectionStringAndNoDefault()
    {
        var sp = ProviderWith(new StubAccessor(new TenantInfo("1", "acme", ConnectionString: null)));

        Assert.Throws<InvalidOperationException>(() => DapperConnectionString.Resolve(ConfigWithDefault(null), sp));
    }
}
