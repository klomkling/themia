using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Themia.Exceptional.Retention;
using Xunit;

namespace Themia.Exceptional.Tests;

public sealed class ExceptionRetentionTests
{
    private sealed class FakePurger(params object[] results) : IRetentionPurger
    {
        private readonly Queue<object> queue = new(results);
        public List<(DateTime Cutoff, int BatchSize)> Calls { get; } = [];

        public Task<int> PurgeBatchAsync(DateTime olderThanUtc, int batchSize, CancellationToken cancellationToken)
        {
            Calls.Add((olderThanUtc, batchSize));
            var next = queue.Count > 0 ? queue.Dequeue() : batchSize;
            return next is Exception ex ? Task.FromException<int>(ex) : Task.FromResult((int)next);
        }
    }

    private sealed class CapturingLogger : ILogger<ExceptionRetentionService>
    {
        public List<(LogLevel Level, string Message, Exception? Exception)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add((logLevel, formatter(state, exception), exception));
    }

    private static ExceptionalOptions Options(Action<ExceptionRetentionOptions>? configure = null)
    {
        var o = new ExceptionalOptions { ApplicationName = "App" };
        o.Retention.Enabled = true;
        o.Retention.BatchSize = 10;
        o.Retention.MaxBatchesPerSweep = 3;
        configure?.Invoke(o.Retention);
        return o;
    }

    private static ExceptionRetentionService Service(
        IRetentionPurger purger, ExceptionalOptions options, CapturingLogger? logger = null, TimeProvider? time = null)
        => new(purger, options, time ?? TimeProvider.System, logger ?? new CapturingLogger(), (_, _) => Task.CompletedTask);

    [Fact]
    public void Defaults_AreOff()
    {
        var o = new ExceptionalOptions { ApplicationName = "App" };

        Assert.False(o.Retention.Enabled);
        Assert.Equal(30, o.Retention.Days);
        Assert.Equal(TimeSpan.FromDays(1), o.Retention.SweepInterval);
        o.Validate();
    }

    [Theory]
    [InlineData(0, 1000, 100, 1440)]
    [InlineData(30, 0, 100, 1440)]
    [InlineData(30, 4001, 100, 1440)]
    [InlineData(30, 1000, 0, 1440)]
    [InlineData(30, 1000, 100, 0)]
    [InlineData(36501, 1000, 100, 1440)]
    [InlineData(30, 1000, 100, 43201)]
    public void Validate_Rejects_BadRetentionOptions(int days, int batch, int maxBatches, int intervalMinutes)
    {
        var o = Options(r =>
        {
            r.Days = days; r.BatchSize = batch; r.MaxBatchesPerSweep = maxBatches; r.SweepInterval = TimeSpan.FromMinutes(intervalMinutes);
        });

        Assert.Throws<InvalidOperationException>(o.Validate);
    }

    [Fact]
    public void Validate_Rejects_DaysNotLongerThanRollupPeriod()
    {
        var o = Options(r => r.Days = 1);
        o.RollupPeriod = TimeSpan.FromDays(1);

        Assert.Throws<InvalidOperationException>(o.Validate);
    }

    [Fact]
    public void Validate_IgnoresRetentionValues_WhenDisabled()
    {
        var o = Options(r => { r.Enabled = false; r.Days = 0; });

        o.Validate();
    }

    [Fact]
    public async Task Sweep_UsesNowMinusDaysAsCutoff()
    {
        var purger = new FakePurger(0);
        var now = new DateTimeOffset(2026, 11, 10, 12, 0, 0, TimeSpan.Zero);

        await Service(purger, Options(r => r.Days = 7), time: new FixedTime(now)).SweepAsync(default);

        Assert.Equal(now.UtcDateTime.AddDays(-7), purger.Calls.Single().Cutoff);
    }

    [Fact]
    public async Task Sweep_StopsAtFirstShortBatch()
    {
        var purger = new FakePurger(10, 4);

        await Service(purger, Options()).SweepAsync(default);

        Assert.Equal(2, purger.Calls.Count);
    }

    [Fact]
    public async Task Sweep_StopsAtMaxBatchesPerSweep()
    {
        var purger = new FakePurger();

        await Service(purger, Options()).SweepAsync(default);

        Assert.Equal(3, purger.Calls.Count);
    }

    [Fact]
    public async Task Sweep_LogsFailureWithTypeNameOnly_AndDoesNotThrow()
    {
        var logger = new CapturingLogger();
        var purger = new FakePurger(new InvalidOperationException("secret-connection-detail"));

        await Service(purger, Options(), logger).SweepAsync(default);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Null(entry.Exception);
        Assert.DoesNotContain("secret-connection-detail", entry.Message);
        Assert.Contains("InvalidOperationException", entry.Message);
    }

    [Fact]
    public async Task Sweep_TreatsANonStoppingCancellationFromThePurgerAsAFailure()
    {
        var logger = new CapturingLogger();

        await Service(new FakePurger(new OperationCanceledException()), Options(), logger).SweepAsync(default);

        Assert.Equal(LogLevel.Warning, Assert.Single(logger.Entries).Level);
    }

    [Fact]
    public async Task Sweep_LogsInformationWithCounts_WhenRowsDeleted()
    {
        var logger = new CapturingLogger();

        await Service(new FakePurger(3), Options(), logger).SweepAsync(default);

        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Contains("deleted 3 rows", entry.Message);
    }

    [Fact]
    public async Task Sweep_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Service(new FakePurger(), Options()).SweepAsync(cts.Token));
    }

    [Fact]
    public async Task Service_RunsASweepAfterTheStartDelay_ThenStopsOnCancel()
    {
        var purger = new FakePurger(0);
        using var cts = new CancellationTokenSource();
        var delays = 0;
        var service = new ExceptionRetentionService(purger, Options(), TimeProvider.System, new CapturingLogger(), (_, ct) =>
        {
            if (++delays == 3)
                cts.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });

        await service.StartAsync(cts.Token);
        await service.ExecuteTask!;

        Assert.True(purger.Calls.Count >= 2);
    }

    [Fact]
    public void Registration_AddsNoHostedService_WhenDisabled()
    {
        var services = new ServiceCollection();

        services.AddThemiaExceptionalCore(o => o.ApplicationName = "App");

        Assert.DoesNotContain(services, d => d.ServiceType == typeof(IHostedService));
    }

    [Fact]
    public void Registration_AddsHostedService_WhenEnabled()
    {
        var services = new ServiceCollection();

        services.AddThemiaExceptionalCore(o => { o.ApplicationName = "App"; o.Retention.Enabled = true; });

        Assert.Contains(services, d => d.ServiceType == typeof(IHostedService));
    }

    [Fact]
    public void Registration_ResolvesTheHostedService_FromTheContainer()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IRetentionPurger>(new FakePurger());
        services.AddThemiaExceptionalCore(o => { o.ApplicationName = "App"; o.Retention.Enabled = true; });
        using var provider = services.BuildServiceProvider();

        Assert.Contains(provider.GetServices<IHostedService>(), h => h is ExceptionRetentionService);
    }

    [Fact]
    public void Registration_FailsOnStart_ForInvalidRetentionOptions()
    {
        var services = new ServiceCollection();

        Assert.Throws<InvalidOperationException>(() =>
            services.AddThemiaExceptionalCore(o => { o.ApplicationName = "App"; o.Retention.Enabled = true; o.Retention.Days = 0; }));
    }

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
