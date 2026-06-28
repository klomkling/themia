using Quartz;
using Quartz.Impl.Matchers;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Modules.Export.Requests;
using Themia.Modules.Export.Store;
using Xunit;

namespace Themia.Modules.Export.Tests;

public sealed class ExportRequestServiceTests : IClassFixture<ExportDbFixture>
{
    private readonly ExportDbFixture fixture;

    public ExportRequestServiceTests(ExportDbFixture fixture) => this.fixture = fixture;

    [Fact]
    public async Task Submit_creates_pending_run_and_schedules_one_shot_job()
    {
        await fixture.ResetAsync();
        var scheduler = await fixture.NewMemoryScheduler();
        var service = fixture.BuildRequestService(scheduler, tenant: new TenantId("acme"), definitions: ["sales"]);

        var view = await service.SubmitAsync(new ExportSubmission("sales", null, ExportFormat.Csv, UserId: "u1"), default);

        Assert.Equal(ExportRunStatus.Pending, view.Status);
        var keys = await scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup());
        Assert.NotEmpty(keys); // a one-shot job was scheduled
    }

    [Fact]
    public async Task Submit_rejects_soft_delete_when_definition_disallows()
    {
        await fixture.ResetAsync();
        var scheduler = await fixture.NewMemoryScheduler();
        var service = fixture.BuildRequestService(scheduler, tenant: new TenantId("acme"), definitions: ["sales"]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SubmitAsync(new ExportSubmission("sales", null, ExportFormat.Csv, IncludeSoftDeleted: true), default));
    }

    [Fact]
    public async Task Submit_rejects_unknown_definition_key()
    {
        await fixture.ResetAsync();
        var scheduler = await fixture.NewMemoryScheduler();
        var service = fixture.BuildRequestService(scheduler, tenant: new TenantId("acme"), definitions: ["sales"]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SubmitAsync(new ExportSubmission("nope", null, ExportFormat.Csv), default));
    }

    [Fact]
    public async Task Schedule_persists_schedule_and_registers_cron_trigger()
    {
        await fixture.ResetAsync();
        var scheduler = await fixture.NewMemoryScheduler();
        var service = fixture.BuildRequestService(scheduler, tenant: new TenantId("acme"), definitions: ["sales"]);

        var id = await service.ScheduleAsync(
            new ExportScheduleRequest("sales", "0 0 6 * * ?", ExportFormat.Csv, UserId: "u1"), default);

        Assert.NotEqual(Guid.Empty, id);
        var keys = await scheduler.GetJobKeys(GroupMatcher<JobKey>.AnyGroup());
        Assert.NotEmpty(keys); // a cron job was registered

        // Schedule was persisted: read it back cross-tenant with no ambient tenant set.
        TenantContextAccessor.CurrentTenantId = null;
        await using var read = fixture.NewContext();
        var store = new ExportScheduleStore(read, new DataFilterScope());
        var saved = await store.GetByIdIgnoringTenantAsync(id, default);
        Assert.NotNull(saved);
        Assert.Equal("0 0 6 * * ?", saved!.Cron);
        Assert.Equal(new TenantId("acme"), saved.TenantId);
    }
}
