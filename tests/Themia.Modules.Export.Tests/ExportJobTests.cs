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
            var store = new ExportRunStore(ctx, new DataFilterScope());
            await store.CreateAsync(
                new ExportRun
                {
                    DefinitionKey = "stub",
                    Format = ExportFormat.Csv,
                    Status = ExportRunStatus.Pending,
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
            var run = await new ExportRunStore(read, new DataFilterScope()).GetByIdIgnoringTenantAsync(id, default);
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
            var store = new ExportRunStore(ctx, new DataFilterScope());
            await store.CreateAsync(
                new ExportRun
                {
                    DefinitionKey = "throw",
                    Format = ExportFormat.Csv,
                    Status = ExportRunStatus.Pending,
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
            var run = await new ExportRunStore(read, new DataFilterScope()).GetByIdIgnoringTenantAsync(id, default);
            Assert.Equal(ExportRunStatus.Failed, run!.Status);
            Assert.NotNull(run.Error);
        }

        Assert.True(notifier.Dispatched);
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

internal sealed class FakeTenantStorage : ITenantStorage
{
    public bool PutCalled { get; private set; }

    public Task<StoredObject> PutAsync(string key, Stream content, StoragePutOptions options, CancellationToken cancellationToken = default)
    {
        PutCalled = true;
        return Task.FromResult(new StoredObject(Guid.NewGuid(), key, 0, options.ContentType));
    }

    public Task<Uri> GetDownloadUrlAsync(string key, TimeSpan expiry, CancellationToken cancellationToken = default)
        => Task.FromResult(new Uri("https://x/dl"));

    public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

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

/// <summary>Minimal <see cref="IJobExecutionContext"/> stub: only <see cref="MergedJobDataMap"/> and
/// <see cref="CancellationToken"/> are implemented; all other members throw.</summary>
internal sealed class FakeJobContext : IJobExecutionContext
{
    private FakeJobContext(JobDataMap map) => MergedJobDataMap = map;

    public static FakeJobContext WithRunId(Guid id)
    {
        var map = new JobDataMap();
        map[ExportJob.RunIdKey] = id.ToString();
        return new FakeJobContext(map);
    }

    public JobDataMap MergedJobDataMap { get; }
    public CancellationToken CancellationToken => CancellationToken.None;

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
        var store = new ExportRunStore(ctx, new DataFilterScope());
        var registry = new ExportDefinitionRegistry([definition]);
        var opts = Options.Create(new ExportModuleOptions
        {
            Retention = TimeSpan.FromDays(7),
            LinkTtl = TimeSpan.FromHours(1),
        });
        return new ExportJob(store, registry, storage, notifier, new DataFilterScope(), opts, NullLogger<ExportJob>.Instance);
    }
}
