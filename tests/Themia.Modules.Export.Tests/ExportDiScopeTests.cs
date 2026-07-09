using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Framework.Data.EFCore.Abstractions;
using Themia.Modules.Export.DependencyInjection;
using Themia.Modules.Export.Store;
using Xunit;

namespace Themia.Modules.Export.Tests;

public sealed class ExportDiScopeTests
{
    /// <summary>
    /// Regression for #147. The export store must resolve under ASP.NET Core Development scope validation
    /// (`ValidateScopes = true`): the context is registered scoped (not through a singleton
    /// <c>IDbContextFactory</c>), so its scoped <see cref="Framework.Core.Abstractions.Tenancy.ITenantContext"/>
    /// dependency resolves from the request/job scope rather than the root provider. Under the old
    /// singleton-factory registration this threw "Cannot resolve scoped service 'ITenantContext' from root provider".
    /// </summary>
    [Fact]
    public void Export_store_resolves_under_scope_validation()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = "Host=localhost;Database=x;Username=u;Password=p",
            })
            .Build();

        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<IDatabaseProvider>(new FakePostgresProvider());
        services.AddThemiaExportModule();

        using var provider = services.BuildServiceProvider(
            new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = false });
        using var scope = provider.CreateScope();

        var store = scope.ServiceProvider.GetRequiredService<IExportRunStore>();

        Assert.NotNull(store);
    }

    private sealed class FakePostgresProvider : IDatabaseProvider
    {
        public string ProviderName => DatabaseProviderNames.Postgres;

        public void Configure(DbContextOptionsBuilder optionsBuilder, IConfiguration configuration, IServiceProvider serviceProvider)
        {
        }

        public void ConfigureServices(IServiceCollection services, IConfiguration configuration)
        {
        }
    }
}
