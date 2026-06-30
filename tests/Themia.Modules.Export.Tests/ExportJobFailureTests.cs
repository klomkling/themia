using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Modules.Export.Definitions;
using Themia.Modules.Export.Entities;
using Themia.Modules.Export.Jobs;
using Themia.Modules.Export.Store;
using Xunit;

namespace Themia.Modules.Export.Tests;

/// <summary>Unit tests for <see cref="ExportJob"/>'s failure path that do not need a database.</summary>
public sealed class ExportJobFailureTests
{
    [Fact]
    public async Task Execute_does_not_throw_when_persisting_the_failed_status_itself_fails()
    {
        var run = new ExportRun
        {
            DefinitionKey = "throw",
            Format = ExportFormat.Csv,
            TenantId = new TenantId("acme"),
            UserId = "u1",
        }.WithId(Guid.NewGuid());

        var store = new FailingUpdateRunStore(run);
        var job = new ExportJob(
            store,
            new ExportDefinitionRegistry([new ThrowingDefinition()]),
            new FakeTenantStorage(),
            new FakeDispatcher(),
            new DataFilterScope(),
            Options.Create(new ExportModuleOptions()),
            NullLogger<ExportJob>.Instance);

        // The definition throws; the failure handler's UpdateAsync(Failed) ALSO throws. Execute must still
        // complete — the secondary failure is logged, never propagated, so it cannot mask the original cause.
        await job.Execute(FakeJobContext.WithRunId(run.Id));

        Assert.Equal(ExportRunStatus.Failed, run.Status); // run was marked Failed before the persist threw
        Assert.Equal(2, store.UpdateCalls);                // Running (ok) + Failed (threw)
    }
}

/// <summary>An <see cref="IExportRunStore"/> whose <see cref="UpdateAsync"/> throws when persisting a
/// Failed status, simulating a database outage during the failure-recording path.</summary>
internal sealed class FailingUpdateRunStore(ExportRun seed) : IExportRunStore
{
    public int UpdateCalls { get; private set; }

    public Task<ExportRun> CreateAsync(ExportRun run, CancellationToken cancellationToken)
        => Task.FromResult(run);

    public Task<ExportRun?> GetByIdAsync(Guid id, CancellationToken cancellationToken)
        => Task.FromResult<ExportRun?>(seed);

    public Task<ExportRun?> GetByIdIgnoringTenantAsync(Guid id, CancellationToken cancellationToken)
        => Task.FromResult<ExportRun?>(seed);

    public Task UpdateAsync(ExportRun run, CancellationToken cancellationToken)
    {
        UpdateCalls++;
        if (run.Status == ExportRunStatus.Failed)
        {
            throw new InvalidOperationException("Database unavailable while recording the failure.");
        }

        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<ExportRun>> ListAsync(string? userId, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<ExportRun>>([]);

    public Task<IReadOnlyList<ExportRun>> FindExpiredAcrossTenantsAsync(DateTimeOffset now, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<ExportRun>>([]);

    public Task<IReadOnlyList<ExportRun>> FindStaleRunningAcrossTenantsAsync(DateTimeOffset cutoff, CancellationToken cancellationToken)
        => Task.FromResult<IReadOnlyList<ExportRun>>([]);
}
