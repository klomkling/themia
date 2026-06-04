using Microsoft.Extensions.DependencyInjection;
using Themia.Caching;
using Themia.Services;
using Themia.Services.Abstractions;
using Xunit;

namespace Themia.Services.Tests;

public class ServicesContractsTests
{
    [Fact]
    public void All_marker_interfaces_inherit_IService()
    {
        Assert.True(typeof(IService).IsAssignableFrom(typeof(IInfrastructureService)));
        Assert.True(typeof(IService).IsAssignableFrom(typeof(IDomainService)));
        Assert.True(typeof(IService).IsAssignableFrom(typeof(IIntegrationService)));
    }

    [Fact]
    public void EmailService_contract_is_an_infrastructure_service()
    {
        Assert.True(typeof(IInfrastructureService).IsAssignableFrom(typeof(IEmailService)));
    }

    [Fact]
    public void CacheProviderAccessor_exposes_ThemiaCacheProvider()
    {
        var property = typeof(ICacheProviderAccessor).GetProperty(nameof(ICacheProviderAccessor.Provider));
        Assert.NotNull(property);
        Assert.Equal(typeof(IThemiaCacheProvider), property!.PropertyType);
    }

    [Fact]
    public void AddThemiaServices_runs_without_throwing_and_registers_no_defaults()
    {
        var services = new ServiceCollection();

        services.AddThemiaServices();

        using var provider = services.BuildServiceProvider();
        // No infra contract has a default implementation in 0.2.0.
        Assert.Null(provider.GetService<IEmailService>());
    }
}
