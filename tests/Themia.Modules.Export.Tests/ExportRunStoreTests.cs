using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Modules.Export.Entities;
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
            var store = new ExportRunStore(ctx, new DataFilterScope());
            await store.CreateAsync(
                new ExportRun
                {
                    Format = ExportFormat.Csv,
                    Status = ExportRunStatus.Pending,
                    DefinitionKey = "k",
                    TenantId = new TenantId("acme"),
                    CreatedAt = DateTimeOffset.UtcNow,
                }.WithId(id),
                default);
        }

        TenantContextAccessor.CurrentTenantId = null; // background: no ambient tenant yet
        await using (var ctx = fixture.NewContext())
        {
            var store = new ExportRunStore(ctx, new DataFilterScope());
            var run = await store.GetByIdIgnoringTenantAsync(id, default);
            Assert.NotNull(run);
            Assert.Equal(new TenantId("acme"), run!.TenantId);
        }
    }
}
