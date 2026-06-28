using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Filtering;
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
            await new ExportRunStore(ctx, new DataFilterScope()).CreateAsync(new ExportRun
            {
                TenantId = new TenantId("acme"), DefinitionKey = "k", Format = ExportFormat.Csv,
                Status = ExportRunStatus.Succeeded, StorageKey = "exports/acme/x.csv",
                ExpiresAt = DateTimeOffset.UtcNow.AddDays(-1), CreatedAt = DateTimeOffset.UtcNow.AddDays(-8),
            }.WithId(id), default);
        }

        var storage = new FakeTenantStorage();
        var job = fixture.BuildCleanupJob(storage);
        await job.Execute(FakeJobContext.Empty());

        Assert.Contains("exports/acme/x.csv", storage.Deleted);
        await using var read = fixture.NewContext();
        using (BackgroundTenantScope.Begin(new TenantId("acme")))
        {
            var run = await new ExportRunStore(read, new DataFilterScope()).GetByIdIgnoringTenantAsync(id, default);
            Assert.Equal(ExportRunStatus.Expired, run!.Status);
        }
    }
}
