using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Modules.Export;
using Themia.Modules.Export.Entities;
using Themia.Modules.Export.Jobs;
using Themia.Modules.Export.Store;
using Xunit;

namespace Themia.Modules.Export.Tests;

public sealed class CleanupJobTests : IClassFixture<ExportDbFixture>
{
    private readonly ExportDbFixture fixture;
    public CleanupJobTests(ExportDbFixture fixture) => this.fixture = fixture;

    [Fact]
    public async Task Deletes_expired_blobs_and_marks_runs_expired()
    {
        await fixture.ResetAsync();
        var id = Guid.NewGuid();
        using (BackgroundTenantScope.Begin(new TenantId("acme")))
        await using (var ctx = fixture.NewContext())
        {
            var run = new ExportRun
            {
                TenantId = new TenantId("acme"), DefinitionKey = "k", Format = ExportFormat.Csv,
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-8),
            }.WithId(id);
            run.MarkSucceeded("exports/acme/x.csv", "x.csv", 0,
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(-1));
            await new ExportRunStore(ctx).CreateAsync(run, default);
        }

        var storage = new FakeTenantStorage();
        var job = fixture.BuildCleanupJob(storage);
        await job.Execute(FakeJobContext.Empty());

        Assert.Contains("exports/acme/x.csv", storage.Deleted);
        await using var read = fixture.NewContext();
        using (BackgroundTenantScope.Begin(new TenantId("acme")))
        {
            var run = await new ExportRunStore(read).GetByIdIgnoringTenantAsync(id, default);
            Assert.Equal(ExportRunStatus.Expired, run!.Status);
        }
    }

    [Fact]
    public async Task A_failed_blob_delete_isolates_that_run_and_the_sweep_continues_across_tenants()
    {
        await fixture.ResetAsync();
        var failingId = await SeedExpiredAsync("acme", "exports/acme/a.csv");
        var okId = await SeedExpiredAsync("globex", "exports/globex/b.csv");

        // The first tenant's blob delete fails; the sweep must still process the second tenant's run.
        var storage = new FakeTenantStorage { ThrowDeleteKey = "exports/acme/a.csv" };
        var job = fixture.BuildCleanupJob(storage);
        await job.Execute(FakeJobContext.Empty());

        await using var read = fixture.NewContext();
        var store = new ExportRunStore(read);

        // The failing run is isolated: its blob delete threw, so it is NOT marked Expired (its bytes remain).
        var failing = await store.GetByIdIgnoringTenantAsync(failingId, default);
        Assert.Equal(ExportRunStatus.Succeeded, failing!.Status);
        Assert.DoesNotContain("exports/acme/a.csv", storage.Deleted);

        // The other tenant's run was still swept despite the earlier failure.
        var ok = await store.GetByIdIgnoringTenantAsync(okId, default);
        Assert.Equal(ExportRunStatus.Expired, ok!.Status);
        Assert.Contains("exports/globex/b.csv", storage.Deleted);
    }

    private async Task<Guid> SeedExpiredAsync(string tenant, string storageKey)
    {
        var id = Guid.NewGuid();
        using (BackgroundTenantScope.Begin(new TenantId(tenant)))
        await using (var ctx = fixture.NewContext())
        {
            var run = new ExportRun
            {
                TenantId = new TenantId(tenant), DefinitionKey = "k", Format = ExportFormat.Csv,
                CreatedAt = DateTimeOffset.UtcNow.AddDays(-8),
            }.WithId(id);
            run.MarkSucceeded(storageKey, "x.csv", 0,
                DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(-1));
            await new ExportRunStore(ctx).CreateAsync(run, default);
        }

        return id;
    }
}
