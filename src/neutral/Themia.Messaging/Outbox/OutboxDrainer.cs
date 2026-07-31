using System.Data.Common;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Themia.Messaging.Outbox;

/// <summary>
/// Background service that drains a transactional outbox: it claims due rows under a lease, hands each
/// to the <see cref="IOutboxDispatcher{TRow}"/>, and marks the row complete or failed (with backoff).
/// It owns the delivery outcome — failures are recorded on the row, not rethrown.
/// </summary>
/// <typeparam name="TRow">The claimed-row shape this drainer serves.</typeparam>
/// <param name="dialect">Engine-specific claim/complete/fail SQL for this outbox.</param>
/// <param name="dispatcher">Delivers a claimed row and classifies failures.</param>
/// <param name="signal">In-process wake kicked after an enqueuing transaction commits.</param>
/// <param name="scopeFactory">Creates the per-batch scope delivery dependencies resolve from.</param>
/// <param name="options">Drain-loop settings.</param>
/// <param name="time">Clock used for lease, completion and backoff timestamps.</param>
/// <param name="logger">Logger for cycle and delivery failures.</param>
/// <param name="purgeDialect">Optional retention purge; when absent, no purge runs regardless of options.</param>
public sealed class OutboxDrainer<TRow>(
    IOutboxDialect<TRow> dialect,
    IOutboxDispatcher<TRow> dispatcher,
    DrainSignal signal,
    IServiceScopeFactory scopeFactory,
    OutboxDrainerOptions<TRow> options,
    TimeProvider time,
    ILogger<OutboxDrainer<TRow>> logger,
    IOutboxPurgeDialect<TRow>? purgeDialect = null) : BackgroundService
    where TRow : IClaimedRow
{
    private const int MaxErrorLength = 1000;

    private readonly string leaseOwner = $"{Environment.MachineName}:{Environment.ProcessId}";
    private DateTimeOffset lastPurgeAt = DateTimeOffset.MinValue;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                int drained;
                do
                {
                    drained = await DrainOnceAsync(stoppingToken).ConfigureAwait(false);
                }
                while (drained == options.MaxBatchSize && !stoppingToken.IsCancellationRequested); // keep draining a full batch

                // Wait for the next signal OR the poll interval, whichever comes first.
                using var pollCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                pollCts.CancelAfter(TimeSpan.FromSeconds(options.DrainIntervalSeconds));
                try
                {
                    await signal.WaitAsync(pollCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!stoppingToken.IsCancellationRequested)
                {
                    // Poll interval elapsed without a signal — drain again.
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // host stop — clean shutdown.
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Outbox drain cycle failed; backing off before retry.");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(options.DrainIntervalSeconds), stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    /// <summary>Claims and dispatches one batch. Returns the number of rows claimed.</summary>
    /// <param name="ct">A token to cancel the cycle.</param>
    /// <returns>The number of rows claimed in this cycle.</returns>
    public async Task<int> DrainOnceAsync(CancellationToken ct)
    {
        var now = time.GetUtcNow();
        var leaseExpires = now.AddSeconds(options.LeaseSeconds);
        // ponytail: one drain connection held across the batch's sends — fine for a single drainer
        // (one open connection at a time); if multiple drainers or slow providers make
        // connection-hold-time matter, claim+close then reopen per result.
        await using var connection = dialect.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);

        var claimed = await dialect.ClaimAsync(connection, leaseOwner, now, leaseExpires, options.MaxBatchSize, ct).ConfigureAwait(false);
        if (claimed.Count == 0)
        {
            await PurgeIfDueAsync(connection, now, ct).ConfigureAwait(false);
            return 0;
        }

        using var scope = scopeFactory.CreateScope();
        foreach (var row in claimed)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                await DeliverAsync(scope.ServiceProvider, connection, row, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw; // shutdown — abort cleanly
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Outbox row {Id} could not be finalized; leaving for lease re-claim.", row.Id);
            }
        }

        await PurgeIfDueAsync(connection, now, ct).ConfigureAwait(false);
        return claimed.Count;
    }

    // Retention runs on the drain loop's own connection and cadence: a dedicated scheduler would force a
    // new package dependency on every adopter purely to delete rows on a timer.
    private async Task PurgeIfDueAsync(DbConnection connection, DateTimeOffset now, CancellationToken ct)
    {
        if (!options.PurgeEnabled || purgeDialect is null)
        {
            return;
        }

        if (now - lastPurgeAt < TimeSpan.FromHours(options.PurgeIntervalHours))
        {
            return;
        }

        var sentDeleted = await PurgeAllAsync(
            (c, cutoff, batch, token) => purgeDialect.PurgeSentAsync(c, cutoff, batch, token),
            connection, now.AddDays(-options.SentRetentionDays), ct).ConfigureAwait(false);

        var deadDeleted = await PurgeAllAsync(
            (c, cutoff, batch, token) => purgeDialect.PurgeDeadAsync(c, cutoff, batch, token),
            connection, now.AddDays(-options.DeadRetentionDays), ct).ConfigureAwait(false);

        // Only advance the gate once both passes complete without throwing — a transient failure
        // (timeout, lock conflict) must not suppress the next attempt for a full PurgeIntervalHours.
        lastPurgeAt = now;

        if (sentDeleted + deadDeleted > 0)
        {
            logger.LogInformation(
                "Outbox purge removed {SentDeleted} sent and {DeadDeleted} dead rows.", sentDeleted, deadDeleted);
        }
    }

    private async Task<int> PurgeAllAsync(
        Func<DbConnection, DateTimeOffset, int, CancellationToken, Task<int>> purge,
        DbConnection connection,
        DateTimeOffset cutoff,
        CancellationToken ct)
    {
        var total = 0;
        int deleted;
        do
        {
            ct.ThrowIfCancellationRequested();
            deleted = await purge(connection, cutoff, options.PurgeBatchSize, ct).ConfigureAwait(false);
            total += deleted;
        }
        // A non-positive batch size can never be "filled", so this stops after one call instead of
        // spinning forever on a dialect that (validly) returns 0 for a 0-row request.
        while (options.PurgeBatchSize > 0 && deleted == options.PurgeBatchSize);

        return total;
    }

    private async Task DeliverAsync(IServiceProvider sp, DbConnection connection, TRow row, CancellationToken ct)
    {
        DispatchResult result;
        try
        {
            result = await dispatcher.DispatchAsync(sp, row, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // host stop — let the cycle observe cancellation, do not record as a failure.
        }
        catch (Exception ex)
        {
            // A dispatcher that throws instead of reporting is treated as retryable: the drainer
            // cannot know whether the fault is permanent, and retrying a transient fault is
            // recoverable whereas dead-lettering a retryable one is not.
            result = DispatchResult.Transient(ex.Message);
        }

        if (result.Outcome == DispatchOutcome.Delivered)
        {
            await dialect.CompleteAsync(connection, row.Id, time.GetUtcNow(), ct).ConfigureAwait(false);
            return;
        }

        await FailRowAsync(connection, row, result, ct).ConfigureAwait(false);
    }

    private async Task FailRowAsync(DbConnection connection, TRow row, DispatchResult result, CancellationToken ct)
    {
        var attempts = row.Attempts + 1;
        var dead = result.Outcome == DispatchOutcome.Permanent || BackoffPolicy.IsDead(attempts, options.MaxAttempts);
        var next = BackoffPolicy.NextAttemptAt(time.GetUtcNow(), attempts);
        var error = result.Error ?? "Dispatcher reported failure.";

        // Log once, with safe context only (no recipient PII, no credentials). The drainer owns the
        // outcome (THEMIA101: no log-and-rethrow) — record it on the row instead of propagating.
        logger.LogWarning(
            "Outbox row {Id} failed (attempt {Attempts}): {Error}; {Outcome}.",
            row.Id,
            attempts,
            error,
            dead ? "dead-lettered" : "will retry");

        await dialect.FailAsync(connection, row.Id, attempts, next, dead, Truncate(error, MaxErrorLength), ct).ConfigureAwait(false);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
