using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Framework.Data.EFCore.Abstractions;
using Themia.Framework.Data.EFCore.Extensions;
using Xunit;

namespace Themia.Framework.Data.EFCore.Tests;

public class AddThemiaDbContextProviderRegistrationTests
{
    private sealed class StubProvider : IDatabaseProvider
    {
        public string ProviderName => DatabaseProviderNames.Postgres;

        public void Configure(DbContextOptionsBuilder o, IConfiguration c, IServiceProvider s)
            => o.UseInMemoryDatabase("provider-reg-test");

        public void ConfigureServices(IServiceCollection s, IConfiguration c) { }
    }

    [Fact]
    public void AddThemiaDbContext_RegistersActiveDatabaseProvider()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder().Build();
        services.AddSingleton<IConfiguration>(configuration);

        services.AddThemiaDbContext<ProviderRegTestDbContext>(new StubProvider(), configuration);

        using var sp = services.BuildServiceProvider();
        var resolved = sp.GetRequiredService<IDatabaseProvider>();
        Assert.Equal(DatabaseProviderNames.Postgres, resolved.ProviderName);
    }

    private sealed class ProviderRegTestDbContext : ThemiaDbContext
    {
        public ProviderRegTestDbContext(DbContextOptions<ProviderRegTestDbContext> options)
            : base(options, null) { }
    }
}
