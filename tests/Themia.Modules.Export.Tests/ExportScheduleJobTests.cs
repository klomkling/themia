using Microsoft.Extensions.Logging.Abstractions;
using Themia.Export;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Filtering;
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
            var store = new ExportScheduleStore(ctx, new DataFilterScope());
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
        var scheduleStore = new ExportScheduleStore(scheduleCtx, new DataFilterScope());
        var job = new ExportScheduleJob(scheduleStore, enqueuer, NullLogger<ExportScheduleJob>.Instance);

        // Act
        await job.Execute(FakeJobContext.WithScheduleId(scheduleId));

        // Assert — the run must be stamped with the schedule's tenant, not null/ambient.
        Assert.Equal(new TenantId("acme"), enqueuer.CapturedTenantId);
    }
}

/// <summary>Captures the tenantId argument passed to <see cref="IExportRunEnqueuer.EnqueueRunAsync"/>.</summary>
internal sealed class RecordingEnqueuer : IExportRunEnqueuer
{
    public TenantId? CapturedTenantId { get; private set; }

    public Task<ExportRun> EnqueueRunAsync(
        string definitionKey,
        string? parametersJson,
        ExportFormat format,
        string? fileName,
        bool includeSoftDeleted,
        string? userId,
        TenantId? tenantId,
        CancellationToken cancellationToken)
    {
        CapturedTenantId = tenantId;
        var run = new ExportRun
        {
            TenantId = tenantId,
            DefinitionKey = definitionKey,
            Format = format,
            Status = ExportRunStatus.Pending,
            CreatedAt = DateTimeOffset.UtcNow,
        };
        run.SetId(Guid.NewGuid());
        return Task.FromResult(run);
    }
}
