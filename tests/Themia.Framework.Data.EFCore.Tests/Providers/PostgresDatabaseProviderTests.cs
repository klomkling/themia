using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Themia.Framework.Core.Abstractions.Entities;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.EFCore.Abstractions;
using Themia.Framework.Data.EFCore.PostgreSql;
using Xunit;

namespace Themia.Framework.Data.EFCore.Tests.Providers;

public sealed class PostgresDatabaseProviderTests
{
    [Fact]
    public void ProviderName_IsPostgres()
    {
        Assert.Equal(DatabaseProviderNames.Postgres, new PostgresDatabaseProvider().ProviderName);
    }

    [Fact]
    public void AddThemiaPostgres_RegistersContext()
    {
        var services = new ServiceCollection();
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = "Host=localhost;Database=themia;Username=themia",
            })
            .Build();

        services.AddThemiaPostgres<ProbeContext>(configuration);

        using var provider = services.BuildServiceProvider();
        Assert.NotNull(provider.GetRequiredService<ProbeContext>());
    }

    [Fact]
    public void NamingSplit_FrameworkSnakeCase_AdopterNameKept_OnPostgres()
    {
        // Offline model introspection against the Npgsql provider (no connection opened). With no global
        // convention, framework columns are explicitly snake_case and the adopter column keeps its name.
        var options = new DbContextOptionsBuilder<ProbeContext>()
            .UseNpgsql("Host=localhost;Database=themia;Username=themia")
            .Options;
        using var ctx = new ProbeContext(options);

        var entityType = ctx.Model.FindEntityType(typeof(Probe))!;
        var store = StoreObjectIdentifier.Create(entityType, StoreObjectType.Table)!.Value;

        Assert.Equal("tenant_id", entityType.FindProperty(nameof(Probe.TenantId))!.GetColumnName(store));
        Assert.Equal("created_at", entityType.FindProperty("CreatedAt")!.GetColumnName(store));
        Assert.Equal("AppName", entityType.FindProperty(nameof(Probe.AppName))!.GetColumnName(store));
    }

    [Fact]
    public void GlobalSnakeCase_ViaConfigureOptions_SnakeCasesAdopterColumns()
    {
        // The legacy whole-model behavior is opted into through the standard EF mechanism: the adopter
        // references EFCore.NamingConventions and applies it via configureOptions (here: directly on the
        // options builder, which is what AddThemiaPostgres's configureOptions delegate receives).
        var options = new DbContextOptionsBuilder<ProbeContext>()
            .UseNpgsql("Host=localhost;Database=themia;Username=themia")
            .UseSnakeCaseNamingConvention()
            .Options;
        using var ctx = new ProbeContext(options);

        var entityType = ctx.Model.FindEntityType(typeof(Probe))!;
        var store = StoreObjectIdentifier.Create(entityType, StoreObjectType.Table)!.Value;

        Assert.Equal("tenant_id", entityType.FindProperty(nameof(Probe.TenantId))!.GetColumnName(store));
        Assert.Equal("app_name", entityType.FindProperty(nameof(Probe.AppName))!.GetColumnName(store));
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
