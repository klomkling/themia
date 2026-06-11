using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Framework.Core.Abstractions.Entities;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.EFCore.Abstractions;
using Themia.Framework.Data.EFCore.SqlServer;
using Xunit;

namespace Themia.Framework.Data.EFCore.Tests.Providers;

public sealed class SqlServerDatabaseProviderTests
{
    [Fact]
    public void ProviderName_IsSqlServer()
    {
        Assert.Equal(DatabaseProviderNames.SqlServer, new SqlServerDatabaseProvider().ProviderName);
    }

    [Fact]
    public void AddThemiaSqlServer_RegistersContext()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = "Server=localhost;Database=themia;Trusted_Connection=True;TrustServerCertificate=True",
            })
            .Build();

        services.AddThemiaSqlServer<ProbeContext>(configuration);

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<ProbeContext>());
    }

    [Fact]
    public void NamingSplit_FrameworkSnakeCase_AdopterPascalCase_OnSqlServer()
    {
        // Offline model introspection against the SQL Server provider (no connection opened).
        var options = new DbContextOptionsBuilder<ProbeContext>()
            .UseSqlServer("Server=localhost;Database=themia;TrustServerCertificate=True")
            .Options;
        using var ctx = new ProbeContext(options);

        var entityType = ctx.Model.FindEntityType(typeof(Probe))!;
        var store = StoreObjectIdentifier.Create(entityType, StoreObjectType.Table)!.Value;

        Assert.Equal("tenant_id", entityType.FindProperty(nameof(Probe.TenantId))!.GetColumnName(store));
        Assert.Equal("created_at", entityType.FindProperty("CreatedAt")!.GetColumnName(store));
        Assert.Equal("AppName", entityType.FindProperty(nameof(Probe.AppName))!.GetColumnName(store));
    }

    private sealed class Probe : SoftDeletableEntity<int>, ITenantEntity
    {
        public TenantId? TenantId { get; set; }
        public string AppName { get; set; } = string.Empty;
    }

    private sealed class ProbeContext(DbContextOptions options) : ThemiaDbContext(options)
    {
        public DbSet<Probe> Probes => Set<Probe>();
    }
}
