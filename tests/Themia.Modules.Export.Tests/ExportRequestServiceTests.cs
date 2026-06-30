using Microsoft.EntityFrameworkCore;
using Quartz;
using Quartz.Impl.Matchers;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Modules.Export.Definitions;
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
        var store = new ExportScheduleStore(read);
        var saved = await store.GetByIdIgnoringTenantAsync(id, default);
        Assert.NotNull(saved);
        Assert.Equal("0 0 6 * * ?", saved!.Cron);
        Assert.Equal(new TenantId("acme"), saved.TenantId);
    }

    [Fact]
    public async Task Schedule_rejects_invalid_cron_and_persists_no_row()
    {
        await fixture.ResetAsync();
        var scheduler = await fixture.NewMemoryScheduler();
        var service = fixture.BuildRequestService(scheduler, tenant: new TenantId("acme"), definitions: ["sales"]);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.ScheduleAsync(new ExportScheduleRequest("sales", "not a cron", ExportFormat.Csv), default));

        await using var read = fixture.NewContext();
        Assert.Equal(0, await read.Schedules.IgnoreQueryFilters().CountAsync());
    }

    [Fact]
    public async Task Submit_marks_run_failed_when_scheduling_throws()
    {
        await fixture.ResetAsync();
        await using var ctx = fixture.NewContext();
        var service = new ExportRequestService(
            new ExportRunStore(ctx),
            new ExportScheduleStore(ctx),
            new ExportDefinitionRegistry([new StubRequestDefinition("sales")]),
            new ThrowingSchedulerFactory(),
            new TenantContext(new TenantId("acme")));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            service.SubmitAsync(new ExportSubmission("sales", null, ExportFormat.Csv, UserId: "u1"), default));

        // The persisted run must be compensated to Failed — never left orphaned in Pending with no job.
        await using var read = fixture.NewContext();
        var run = await read.Runs.IgnoreQueryFilters().SingleAsync();
        Assert.Equal(ExportRunStatus.Failed, run.Status);
        Assert.NotNull(run.Error);
    }

    [Fact]
    public async Task GetRun_returns_the_run_in_the_current_tenant()
    {
        await fixture.ResetAsync();
        var scheduler = await fixture.NewMemoryScheduler();
        var service = fixture.BuildRequestService(scheduler, tenant: new TenantId("acme"), definitions: ["sales"]);

        TenantContextAccessor.CurrentTenantId = new TenantId("acme");
        var view = await service.SubmitAsync(new ExportSubmission("sales", null, ExportFormat.Csv, UserId: "u1"), default);

        var got = await service.GetRunAsync(view.Id, default);
        Assert.NotNull(got);
        Assert.Equal(view.Id, got!.Id);
    }
}

/// <summary>An <see cref="ISchedulerFactory"/> that always fails to hand back a scheduler (drives the
/// create-then-schedule compensation test).</summary>
internal sealed class ThrowingSchedulerFactory : ISchedulerFactory
{
    public Task<IScheduler> GetScheduler(CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("Scheduler unavailable.");

    public Task<IScheduler?> GetScheduler(string schedName, CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("Scheduler unavailable.");

    public Task<IReadOnlyList<IScheduler>> GetAllSchedulers(CancellationToken cancellationToken = default)
        => throw new InvalidOperationException("Scheduler unavailable.");
}
