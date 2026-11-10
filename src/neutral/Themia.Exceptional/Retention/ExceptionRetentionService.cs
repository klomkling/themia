using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Themia.Exceptional.Retention;

/// <summary>
/// Deletes stored exceptions older than <see cref="ExceptionRetentionOptions.Days"/> on a slow cadence, in bounded
/// batches. No lease: each batch is a plain delete, so concurrent instances only delete rows the other already removed.
/// </summary>
internal sealed class ExceptionRetentionService : BackgroundService
{
    private static readonly TimeSpan MaxStartJitter = TimeSpan.FromMinutes(5);

    private readonly IRetentionPurger purger;
    private readonly ExceptionRetentionOptions options;
    private readonly TimeProvider time;
    private readonly ILogger<ExceptionRetentionService> logger;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;

    public ExceptionRetentionService(
        IRetentionPurger purger, ExceptionalOptions options, TimeProvider time, ILogger<ExceptionRetentionService> logger)
        : this(purger, options, time, logger, (span, ct) => Task.Delay(span, time, ct))
    {
    }

    internal ExceptionRetentionService(
        IRetentionPurger purger,
        ExceptionalOptions options,
        TimeProvider time,
        ILogger<ExceptionRetentionService> logger,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        this.purger = purger;
        this.options = options.Retention;
        this.time = time;
        this.logger = logger;
        this.delay = delay;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            // Spread the first sweep so several apps or instances do not start in lockstep.
            await delay(TimeSpan.FromSeconds(Random.Shared.NextDouble() * MaxStartJitter.TotalSeconds), stoppingToken).ConfigureAwait(false);
            while (!stoppingToken.IsCancellationRequested)
            {
                await SweepAsync(stoppingToken).ConfigureAwait(false);
                await delay(options.SweepInterval, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // host stop
        }
    }

    internal async Task SweepAsync(CancellationToken cancellationToken)
    {
        var cutoff = time.GetUtcNow().UtcDateTime.AddDays(-options.Days);
        var deleted = 0;
        var batches = 0;
        try
        {
            int last;
            do
            {
                cancellationToken.ThrowIfCancellationRequested();
                last = await purger.PurgeBatchAsync(cutoff, options.BatchSize, cancellationToken).ConfigureAwait(false);
                deleted += last;
                batches++;
            }
            while (last >= options.BatchSize && batches < options.MaxBatchesPerSweep);
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            // Type name only: passing the exception would let the Serilog sink store its message as a new row.
            logger.LogWarning("Exceptional retention sweep failed with {ExceptionType} after deleting {Deleted} rows.",
                ex.GetType().Name, deleted);
            return;
        }

        if (deleted > 0)
            logger.LogInformation("Exceptional retention sweep deleted {Deleted} rows older than {Days} days in {Batches} batches.",
                deleted, options.Days, batches);
    }
}
