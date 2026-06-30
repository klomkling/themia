using Microsoft.Extensions.Logging.Abstractions;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Modules.Export.Entities;
using Themia.Modules.Export.Jobs;
using Themia.Modules.Export.Requests;
using Themia.Modules.Export.Store;
using Xunit;

namespace Themia.Modules.Export.Tests;

public sealed class ExportScheduleJobTests : IClassFixture<ExportDbFixture>
{
    private readonly ExportDbFixture fixture;

    public ExportScheduleJobTests(ExportDbFixture fixture) => this.fixture = fixture;

    [Fact]
    public async Task Execute_enqueues_run_stamped_with_schedules_tenant()
    {
        await fixture.ResetAsync();

        // Arrange — seed a schedule for tenant "acme" (cross-tenant write, no ambient tenant needed).
        var scheduleId = Guid.NewGuid();
        await using (var ctx = fixture.NewContext())
        {
            var store = new ExportScheduleStore(ctx);
            var schedule = new ExportSchedule
            {
                TenantId = new TenantId("acme"),
                UserId = "u1",
                DefinitionKey = "sales",
                Format = ExportFormat.Csv,
                Cron = "0 0 6 * * ?",
                Enabled = true,
                IncludeSoftDeleted = false,
                CreatedAt = DateTimeOffset.UtcNow,
            };
            schedule.SetId(scheduleId);
            await store.CreateAsync(schedule, default);
        }

        var enqueuer = new RecordingEnqueuer();

        await using var scheduleCtx = fixture.NewContext();
        var scheduleStore = new ExportScheduleStore(scheduleCtx);
        var job = new ExportScheduleJob(scheduleStore, enqueuer, NullLogger<ExportScheduleJob>.Instance);

        // Act
        await job.Execute(FakeJobContext.WithScheduleId(scheduleId));

        // Assert — the run must be stamped with the schedule's tenant, not null/ambient.
        Assert.Equal(new TenantId("acme"), enqueuer.CapturedTenantId);
    }

    [Fact]
    public async Task Execute_does_not_enqueue_when_schedule_is_disabled()
    {
        await fixture.ResetAsync();
        var scheduleId = await SeedScheduleAsync(enabled: false);

        var enqueuer = new RecordingEnqueuer();
        await using var ctx = fixture.NewContext();
        var job = new ExportScheduleJob(new ExportScheduleStore(ctx), enqueuer, NullLogger<ExportScheduleJob>.Instance);

        await job.Execute(FakeJobContext.WithScheduleId(scheduleId));

        Assert.Null(enqueuer.Captured); // a disabled schedule must not produce a run
    }

    [Fact]
    public async Task Execute_does_not_enqueue_when_schedule_is_missing()
    {
        await fixture.ResetAsync();

        var enqueuer = new RecordingEnqueuer();
        await using var ctx = fixture.NewContext();
        var job = new ExportScheduleJob(new ExportScheduleStore(ctx), enqueuer, NullLogger<ExportScheduleJob>.Instance);

        await job.Execute(FakeJobContext.WithScheduleId(Guid.NewGuid()));

        Assert.Null(enqueuer.Captured);
    }

    private async Task<Guid> SeedScheduleAsync(bool enabled)
    {
        var scheduleId = Guid.NewGuid();
        await using var ctx = fixture.NewContext();
        var schedule = new ExportSchedule
        {
            TenantId = new TenantId("acme"),
            UserId = "u1",
            DefinitionKey = "sales",
            Format = ExportFormat.Csv,
            Cron = "0 0 6 * * ?",
            Enabled = enabled,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        schedule.SetId(scheduleId);
        await new ExportScheduleStore(ctx).CreateAsync(schedule, default);
        return scheduleId;
    }
}

/// <summary>Captures the command passed to <see cref="IExportRunEnqueuer.EnqueueRunAsync"/>.</summary>
internal sealed class RecordingEnqueuer : IExportRunEnqueuer
{
    public EnqueueRunCommand? Captured { get; private set; }
    public TenantId? CapturedTenantId => Captured?.TenantId;

    public Task<ExportRun> EnqueueRunAsync(EnqueueRunCommand command, CancellationToken cancellationToken)
    {
        Captured = command;
        var run = new ExportRun
        {
            TenantId = command.TenantId,
            DefinitionKey = command.DefinitionKey,
            Format = command.Format,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        run.SetId(Guid.NewGuid());
        return Task.FromResult(run);
    }
}
