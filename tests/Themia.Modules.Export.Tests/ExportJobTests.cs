using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Quartz;
using Themia.Export;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Modules.Export.Definitions;
using Themia.Modules.Export.Entities;
using Themia.Modules.Export.Jobs;
using Themia.Modules.Export.Store;
using Themia.Modules.Notifications.Dispatch;
using Themia.Modules.Storage;
using Themia.Notifications;
using Themia.Storage;
using Xunit;

namespace Themia.Modules.Export.Tests;

public sealed class ExportJobTests : IClassFixture<ExportDbFixture>
{
    private readonly ExportDbFixture fixture;

    public ExportJobTests(ExportDbFixture fixture) => this.fixture = fixture;

    [Fact]
    public async Task Succeeds_writes_storage_sets_status_and_notifies()
    {
        await fixture.ResetAsync();
        var id = Guid.NewGuid();

        using (BackgroundTenantScope.Begin(new TenantId("acme")))
        await using (var ctx = fixture.NewContext())
        {
            var store = new ExportRunStore(ctx);
            await store.CreateAsync(
                new ExportRun
                {
                    DefinitionKey = "stub",
                    Format = ExportFormat.Csv,
                    TenantId = new TenantId("acme"),
                    UserId = "user1",
                    CreatedAt = DateTimeOffset.UtcNow,
                }.WithId(id),
                default);
        }

        var storage = new FakeTenantStorage();
        var notifier = new FakeDispatcher();
        var job = fixture.BuildExportJob(storage, notifier, new StubDefinition());

        await job.Execute(FakeJobContext.WithRunId(id));

        await using var read = fixture.NewContext();
        using (BackgroundTenantScope.Begin(new TenantId("acme")))
        {
            var run = await new ExportRunStore(read).GetByIdIgnoringTenantAsync(id, default);
            Assert.Equal(ExportRunStatus.Succeeded, run!.Status);
            Assert.NotNull(run.StorageKey);
            Assert.NotNull(run.ExpiresAt);
        }

        Assert.True(storage.PutCalled);
        Assert.True(notifier.Dispatched);
    }

    [Fact]
    public async Task Fails_sets_Failed_status_and_dispatches_failure_notification()
    {
        await fixture.ResetAsync();
        var id = Guid.NewGuid();

        using (BackgroundTenantScope.Begin(new TenantId("acme")))
        await using (var ctx = fixture.NewContext())
        {
            var store = new ExportRunStore(ctx);
            await store.CreateAsync(
                new ExportRun
                {
                    DefinitionKey = "throw",
                    Format = ExportFormat.Csv,
                    TenantId = new TenantId("acme"),
                    UserId = "user1",
                    CreatedAt = DateTimeOffset.UtcNow,
                }.WithId(id),
                default);
        }

        var storage = new FakeTenantStorage();
        var notifier = new FakeDispatcher();
        var job = fixture.BuildExportJob(storage, notifier, new ThrowingDefinition());

        await job.Execute(FakeJobContext.WithRunId(id));

        await using var read = fixture.NewContext();
        using (BackgroundTenantScope.Begin(new TenantId("acme")))
        {
            var run = await new ExportRunStore(read).GetByIdIgnoringTenantAsync(id, default);
            Assert.Equal(ExportRunStatus.Failed, run!.Status);
            Assert.NotNull(run.Error);
        }

        Assert.True(notifier.Dispatched);
    }

    [Fact]
    public async Task Notification_failure_after_a_stored_success_keeps_the_run_succeeded()
    {
        await fixture.ResetAsync();
        var id = await SeedPendingAsync("stub", userId: "user1");

        var storage = new FakeTenantStorage();
        var job = fixture.BuildExportJob(storage, new ThrowingDispatcher(), new StubDefinition());

        // The completion notification throws, but the export already succeeded and is stored — the job must
        // not fault and must not flip the persisted Succeeded status to Failed.
        await job.Execute(FakeJobContext.WithRunId(id));

        Assert.True(storage.PutCalled);
        await using var read = fixture.NewContext();
        using (BackgroundTenantScope.Begin(new TenantId("acme")))
        {
            var run = await new ExportRunStore(read).GetByIdIgnoringTenantAsync(id, default);
            Assert.Equal(ExportRunStatus.Succeeded, run!.Status);
            Assert.NotNull(run.StorageKey);
        }
    }

    [Fact]
    public async Task Cancellation_rethrows_and_leaves_the_run_running()
    {
        await fixture.ResetAsync();
        var id = await SeedPendingAsync("cancel", userId: "user1");

        var job = fixture.BuildExportJob(new FakeTenantStorage(), new FakeDispatcher(), new CancellingDefinition());

        await Assert.ThrowsAsync<OperationCanceledException>(() => job.Execute(FakeJobContext.WithRunId(id)));

        await using var read = fixture.NewContext();
        using (BackgroundTenantScope.Begin(new TenantId("acme")))
        {
            var run = await new ExportRunStore(read).GetByIdIgnoringTenantAsync(id, default);
            Assert.Equal(ExportRunStatus.Running, run!.Status); // cancellation is not a failure
        }
    }

    [Fact]
    public async Task Failure_persists_Failed_even_when_the_context_token_is_cancelled()
    {
        await fixture.ResetAsync();
        var id = await SeedPendingAsync("cancel-throw", userId: "user1");

        using var cts = new CancellationTokenSource();
        var job = fixture.BuildExportJob(new FakeTenantStorage(), new FakeDispatcher(), new CancelThenThrowDefinition(cts));

        // The definition cancels the context token mid-run, then fails. The failure path uses
        // CancellationToken.None, so Failed must still persist (the run is never orphaned in Running).
        await job.Execute(FakeJobContext.WithRunId(id, cts.Token));

        await using var read = fixture.NewContext();
        using (BackgroundTenantScope.Begin(new TenantId("acme")))
        {
            var run = await new ExportRunStore(read).GetByIdIgnoringTenantAsync(id, default);
            Assert.Equal(ExportRunStatus.Failed, run!.Status);
        }
    }

    [Fact]
    public async Task Succeeds_without_a_user_does_not_dispatch_a_notification()
    {
        await fixture.ResetAsync();
        var id = await SeedPendingAsync("stub", userId: null);

        var notifier = new FakeDispatcher();
        var job = fixture.BuildExportJob(new FakeTenantStorage(), notifier, new StubDefinition());

        await job.Execute(FakeJobContext.WithRunId(id));

        await using var read = fixture.NewContext();
        using (BackgroundTenantScope.Begin(new TenantId("acme")))
        {
            var run = await new ExportRunStore(read).GetByIdIgnoringTenantAsync(id, default);
            Assert.Equal(ExportRunStatus.Succeeded, run!.Status);
        }

        Assert.False(notifier.Dispatched); // no user → no notification dispatched
    }

    [Fact]
    public async Task IncludeSoftDeleted_runs_the_export_inside_a_soft_delete_bypass()
    {
        await fixture.ResetAsync();
        var id = await SeedPendingAsync("sd", userId: "user1", includeSoftDeleted: true);

        var definition = new SoftDeleteAssertingDefinition(new DataFilterScope());
        var job = fixture.BuildExportJob(new FakeTenantStorage(), new FakeDispatcher(), definition);

        await job.Execute(FakeJobContext.WithRunId(id));

        Assert.True(definition.BypassWasActive); // the export ran with the soft-delete filter bypassed
        await using var read = fixture.NewContext();
        using (BackgroundTenantScope.Begin(new TenantId("acme")))
        {
            var run = await new ExportRunStore(read).GetByIdIgnoringTenantAsync(id, default);
            Assert.Equal(ExportRunStatus.Succeeded, run!.Status);
        }
    }

    private async Task<Guid> SeedPendingAsync(string definitionKey, string? userId, bool includeSoftDeleted = false)
    {
        var id = Guid.NewGuid();
        using (BackgroundTenantScope.Begin(new TenantId("acme")))
        await using (var ctx = fixture.NewContext())
        {
            await new ExportRunStore(ctx).CreateAsync(
                new ExportRun
                {
                    DefinitionKey = definitionKey,
                    Format = ExportFormat.Csv,
                    TenantId = new TenantId("acme"),
                    UserId = userId,
                    IncludeSoftDeleted = includeSoftDeleted,
                    CreatedAt = DateTimeOffset.UtcNow,
                }.WithId(id),
                default);
        }

        return id;
    }
}

// --- Test doubles ---

internal sealed class StubDefinition : IExportDefinition
{
    public string Key => "stub";
    public bool AllowsIncludeSoftDeleted => false;

    public Task<ExportResult> ExportAsync(ExportContext context, CancellationToken cancellationToken)
        => Task.FromResult(new ExportResult([1, 2, 3], "text/csv", "export.csv"));
}

internal sealed class ThrowingDefinition : IExportDefinition
{
    public string Key => "throw";
    public bool AllowsIncludeSoftDeleted => false;

    public Task<ExportResult> ExportAsync(ExportContext context, CancellationToken cancellationToken)
        => throw new InvalidOperationException("Simulated export failure.");
}

/// <summary>Throws <see cref="OperationCanceledException"/> to exercise the job's cancellation rethrow path.</summary>
internal sealed class CancellingDefinition : IExportDefinition
{
    public string Key => "cancel";
    public bool AllowsIncludeSoftDeleted => false;

    public Task<ExportResult> ExportAsync(ExportContext context, CancellationToken cancellationToken)
        => throw new OperationCanceledException();
}

/// <summary>Cancels the supplied source (simulating a host shutdown mid-run) and then throws a non-cancellation
/// error, so the failure path runs while the job's context token is already cancelled.</summary>
internal sealed class CancelThenThrowDefinition(CancellationTokenSource source) : IExportDefinition
{
    public string Key => "cancel-throw";
    public bool AllowsIncludeSoftDeleted => false;

    public Task<ExportResult> ExportAsync(ExportContext context, CancellationToken cancellationToken)
    {
        source.Cancel();
        throw new InvalidOperationException("Simulated failure after host cancellation.");
    }
}

/// <summary>Allows soft-delete inclusion and records whether the soft-delete bypass was active during export.</summary>
internal sealed class SoftDeleteAssertingDefinition(IDataFilterScope scope) : IExportDefinition
{
    public string Key => "sd";
    public bool AllowsIncludeSoftDeleted => true;
    public bool BypassWasActive { get; private set; }

    public Task<ExportResult> ExportAsync(ExportContext context, CancellationToken cancellationToken)
    {
        BypassWasActive = scope.IsSoftDeleteFilterBypassed;
        return Task.FromResult(new ExportResult([1, 2, 3], "text/csv", "export.csv"));
    }
}

internal sealed class FakeTenantStorage : ITenantStorage
{
    public bool PutCalled { get; private set; }
    public List<string> Deleted { get; } = [];

    /// <summary>When set, <see cref="DeleteAsync"/> throws for this key (simulates a blob-delete failure).</summary>
    public string? ThrowDeleteKey { get; set; }

    public Task<StoredObject> PutAsync(string key, Stream content, StoragePutOptions options, CancellationToken cancellationToken = default)
    {
        PutCalled = true;
        return Task.FromResult(new StoredObject(Guid.NewGuid(), key, 0, options.ContentType));
    }

    public Task<Uri> GetDownloadUrlAsync(string key, TimeSpan expiry, CancellationToken cancellationToken = default)
        => Task.FromResult(new Uri("https://x/dl"));

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
    {
        if (key == ThrowDeleteKey)
        {
            throw new InvalidOperationException($"Simulated blob-delete failure for '{key}'.");
        }

        Deleted.Add(key);
        return Task.CompletedTask;
    }

    public Task<StorageReadResult?> GetAsync(string key, CancellationToken cancellationToken = default)
        => Task.FromResult<StorageReadResult?>(null);

    public Task<bool> ExistsAsync(string key, CancellationToken cancellationToken = default)
        => Task.FromResult(false);

    public Task<Uri> GetUploadUrlAsync(string key, string contentType, long sizeBytes, TimeSpan expiry, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();

    public Task<StoredObject> CompleteUploadAsync(string key, CancellationToken cancellationToken = default)
        => throw new NotImplementedException();
}

internal sealed class FakeDispatcher : INotificationDispatcher
{
    public bool Dispatched { get; private set; }

    public Task DispatchAsync(NotificationRequest request, CancellationToken ct = default)
    {
        Dispatched = true;
        return Task.CompletedTask;
    }
}

/// <summary>A dispatcher that always fails, to exercise the best-effort completion-notification path.</summary>
internal sealed class ThrowingDispatcher : INotificationDispatcher
{
    public Task DispatchAsync(NotificationRequest request, CancellationToken ct = default)
        => throw new InvalidOperationException("Simulated notification failure.");
}

/// <summary>Minimal <see cref="IJobExecutionContext"/> stub: only <see cref="MergedJobDataMap"/> and
/// <see cref="CancellationToken"/> are implemented; all other members throw.</summary>
internal sealed class FakeJobContext : IJobExecutionContext
{
    private readonly CancellationToken cancellationToken;

    private FakeJobContext(JobDataMap map, CancellationToken cancellationToken = default)
    {
        MergedJobDataMap = map;
        this.cancellationToken = cancellationToken;
    }

    public static FakeJobContext Empty() => new FakeJobContext(new JobDataMap());

    public static FakeJobContext WithRunId(Guid id, CancellationToken cancellationToken = default)
    {
        var map = new JobDataMap();
        map[ExportJob.RunIdKey] = id.ToString();
        return new FakeJobContext(map, cancellationToken);
    }

    public static FakeJobContext WithScheduleId(Guid id)
    {
        var map = new JobDataMap();
        map[ExportScheduleJob.ScheduleIdKey] = id.ToString();
        return new FakeJobContext(map);
    }

    public JobDataMap MergedJobDataMap { get; }
    public CancellationToken CancellationToken => cancellationToken;

    // Members not used by ExportJob — throw so any accidental access is surfaced immediately.
    public IScheduler Scheduler => throw new NotImplementedException();
    public ITrigger Trigger => throw new NotImplementedException();
    public ICalendar? Calendar => throw new NotImplementedException();
    public bool Recovering => throw new NotImplementedException();
    public TriggerKey RecoveringTriggerKey => throw new NotImplementedException();
    public int RefireCount => throw new NotImplementedException();
    public IJobDetail JobDetail => throw new NotImplementedException();
    public IJob JobInstance => throw new NotImplementedException();
    public DateTimeOffset FireTimeUtc => throw new NotImplementedException();
    public DateTimeOffset? ScheduledFireTimeUtc => throw new NotImplementedException();
    public DateTimeOffset? PreviousFireTimeUtc => throw new NotImplementedException();
    public DateTimeOffset? NextFireTimeUtc => throw new NotImplementedException();
    public string FireInstanceId => throw new NotImplementedException();
    public object? Result { get => throw new NotImplementedException(); set => throw new NotImplementedException(); }
    public TimeSpan JobRunTime => throw new NotImplementedException();
    public void Put(object key, object objectValue) => throw new NotImplementedException();
    public object? Get(object key) => throw new NotImplementedException();
}

// --- Fixture extension ---

internal static class ExportDbFixtureJobExtensions
{
    internal static ExportJob BuildExportJob(
        this ExportDbFixture fixture,
        ITenantStorage storage,
        INotificationDispatcher notifier,
        IExportDefinition definition)
    {
        var ctx = fixture.NewContext();
        var store = new ExportRunStore(ctx);
        var registry = new ExportDefinitionRegistry([definition]);
        var opts = Options.Create(new ExportModuleOptions
        {
            Retention = TimeSpan.FromDays(7),
            LinkTtl = TimeSpan.FromHours(1),
        });
        return new ExportJob(store, registry, storage, notifier, new DataFilterScope(), opts, NullLogger<ExportJob>.Instance);
    }

    internal static CleanupJob BuildCleanupJob(
        this ExportDbFixture fixture,
        ITenantStorage storage)
    {
        var ctx = fixture.NewContext();
        var store = new ExportRunStore(ctx);
        return new CleanupJob(store, storage, NullLogger<CleanupJob>.Instance);
    }
}
