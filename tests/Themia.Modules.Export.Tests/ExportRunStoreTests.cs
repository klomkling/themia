using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Modules.Export.Entities;
using Themia.Modules.Export.Jobs;
using Themia.Modules.Export.Store;
using Xunit;

namespace Themia.Modules.Export.Tests;

public sealed class ExportRunStoreTests : IClassFixture<ExportDbFixture>
{
    private readonly ExportDbFixture fixture;

    public ExportRunStoreTests(ExportDbFixture fixture) => this.fixture = fixture;

    [Fact]
    public async Task GetByIdIgnoringTenant_finds_run_without_ambient_tenant()
    {
        await fixture.ResetAsync();
        var id = Guid.NewGuid();

        TenantContextAccessor.CurrentTenantId = new TenantId("acme");
        await using (var ctx = fixture.NewContext())
        {
            var store = new ExportRunStore(ctx);
            await store.CreateAsync(
                new ExportRun
                {
                    Format = ExportFormat.Csv,
                    DefinitionKey = "k",
                    TenantId = new TenantId("acme"),
                    CreatedAt = DateTimeOffset.UtcNow,
                }.WithId(id),
                default);
        }

        TenantContextAccessor.CurrentTenantId = null; // background: no ambient tenant yet
        await using (var ctx = fixture.NewContext())
        {
            var store = new ExportRunStore(ctx);
            var run = await store.GetByIdIgnoringTenantAsync(id, default);
            Assert.NotNull(run);
            Assert.Equal(new TenantId("acme"), run!.TenantId);
        }
    }

    [Fact]
    public async Task GetByIdIgnoringTenant_still_hides_soft_deleted_runs()
    {
        await fixture.ResetAsync();
        var id = await SeedAsync("acme", r => r.IsDeleted = true);

        TenantContextAccessor.CurrentTenantId = null;
        await using var ctx = fixture.NewContext();
        var run = await new ExportRunStore(ctx).GetByIdIgnoringTenantAsync(id, default);
        Assert.Null(run); // the re-applied !IsDeleted guard hides it even across tenants
    }

    [Fact]
    public async Task GetById_is_scoped_to_the_current_tenant()
    {
        await fixture.ResetAsync();
        var id = await SeedAsync("acme");

        await using var ctx = fixture.NewContext();
        var store = new ExportRunStore(ctx);

        TenantContextAccessor.CurrentTenantId = new TenantId("acme");
        Assert.NotNull(await store.GetByIdAsync(id, default));

        TenantContextAccessor.CurrentTenantId = new TenantId("globex");
        Assert.Null(await store.GetByIdAsync(id, default)); // another tenant cannot read it
    }

    [Fact]
    public async Task FindExpiredAcrossTenants_returns_only_expired_succeeded_runs()
    {
        await fixture.ResetAsync();
        var past = DateTimeOffset.UtcNow.AddDays(-1);
        var future = DateTimeOffset.UtcNow.AddDays(1);

        var expired = await SeedAsync("acme", r => r.MarkSucceeded("k", "x.csv", 0, past, past));
        await SeedAsync("acme", r => r.MarkSucceeded("k", "x.csv", 0, future, DateTimeOffset.UtcNow)); // not yet expired
        await SeedAsync("acme"); // Pending
        await SeedAsync("acme", r => r.MarkFailed("boom", DateTimeOffset.UtcNow)); // Failed
        await SeedAsync("acme", r =>
        {
            r.MarkSucceeded("k", "x.csv", 0, past, past); // expired but soft-deleted
            r.IsDeleted = true;
        });

        TenantContextAccessor.CurrentTenantId = null;
        await using var ctx = fixture.NewContext();
        var found = await new ExportRunStore(ctx).FindExpiredAcrossTenantsAsync(DateTimeOffset.UtcNow, default);

        Assert.Equal([expired], found.Select(r => r.Id));
    }

    [Fact]
    public async Task FindStaleRunningAcrossTenants_returns_only_runs_started_before_the_cutoff()
    {
        await fixture.ResetAsync();
        var stale = await SeedAsync("acme", r => r.MarkRunning(DateTimeOffset.UtcNow.AddHours(-2)));
        await SeedAsync("acme", r => r.MarkRunning(DateTimeOffset.UtcNow)); // fresh — still actively running
        await SeedAsync("acme"); // Pending
        await SeedAsync("acme", r =>
            r.MarkSucceeded("k", "x.csv", 0, DateTimeOffset.UtcNow.AddDays(1), DateTimeOffset.UtcNow)); // Succeeded

        TenantContextAccessor.CurrentTenantId = null;
        await using var ctx = fixture.NewContext();
        var found = await new ExportRunStore(ctx)
            .FindStaleRunningAcrossTenantsAsync(DateTimeOffset.UtcNow.AddMinutes(-30), default);

        Assert.Equal([stale], found.Select(r => r.Id));
    }

    private async Task<Guid> SeedAsync(string tenant, Action<ExportRun>? configure = null)
    {
        var id = Guid.NewGuid();
        using (BackgroundTenantScope.Begin(new TenantId(tenant)))
        await using (var ctx = fixture.NewContext())
        {
            var run = new ExportRun
            {
                Format = ExportFormat.Csv,
                DefinitionKey = "k",
                TenantId = new TenantId(tenant),
                CreatedAt = DateTimeOffset.UtcNow,
            }.WithId(id);
            configure?.Invoke(run);
            await new ExportRunStore(ctx).CreateAsync(run, default);
        }

        return id;
    }
}
