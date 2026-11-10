using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Themia.Exceptional.Alerts;

/// <summary>
/// Background job that turns newly stored exceptions into one digest message per run. It reads the exception
/// store and keeps its claim state in the database, so it is safe across restarts and several instances, and it
/// never runs in the logging sink or the request path.
/// </summary>
/// <remarks>
/// Delivery is at-least-once: a crash between a successful send and marking the batch sent can repeat one digest.
/// </remarks>
public sealed class ExceptionAlertService : BackgroundService
{
    private const int CandidateFetchFactor = 5;
    private const double IntervalJitter = 0.1;
    private static readonly TimeSpan CleanupEvery = TimeSpan.FromHours(1);
    private static readonly TimeSpan MinimumRetention = TimeSpan.FromDays(1);

    private readonly IExceptionAlertStore store;
    private readonly IAlertChannel channel;
    private readonly ExceptionAlertOptions options;
    private readonly string applicationName;
    private readonly ILogger<ExceptionAlertService> logger;
    private long lastCleanupTicks = long.MinValue;

    internal ExceptionAlertService(
        IExceptionAlertStore store,
        IAlertChannel channel,
        ExceptionAlertOptions options,
        string applicationName,
        ILogger<ExceptionAlertService> logger)
    {
        this.store = store;
        this.channel = channel;
        this.options = options;
        this.applicationName = applicationName;
        this.logger = logger;
    }

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                // One failed run never stops the job; the next interval tries again. Logged once per failed run.
                logger.LogError(ex, "Exception alert run failed.");
            }

            try
            {
                await Task.Delay(JitteredInterval(), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await AbandonExhaustedAsync(cancellationToken).ConfigureAwait(false);
        await CleanupIfDueAsync(cancellationToken).ConfigureAwait(false);

        // The cap is checked before anything is claimed, so capped candidates stay unclaimed and alert later
        // (while still inside the lookback window) instead of burning attempts.
        var sentLastHour = await store.CountSentBatchesLastHourAsync(applicationName, cancellationToken).ConfigureAwait(false);
        if (sentLastHour >= options.MaxMessagesPerHour)
        {
            logger.LogDebug("Exception alert cap reached ({Sent}/{Max} per hour); skipping this run.",
                sentLastHour, options.MaxMessagesPerHour);
            return;
        }

        if (await RetryStaleBatchAsync(cancellationToken).ConfigureAwait(false))
            return;

        await SendNewAlertsAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task AbandonExhaustedAsync(CancellationToken ct)
    {
        var abandoned = await store.FailExhaustedAsync(applicationName, options.Lease, options.MaxAttempts, ct).ConfigureAwait(false);
        if (abandoned > 0)
            logger.LogError("Gave up on {Count} exception alert(s) after {MaxAttempts} attempts.", abandoned, options.MaxAttempts);
    }

    private async Task CleanupIfDueAsync(CancellationToken ct)
    {
        var now = Environment.TickCount64;
        if (lastCleanupTicks != long.MinValue && now - lastCleanupTicks < CleanupEvery.TotalMilliseconds)
            return;
        lastCleanupTicks = now;

        // Kept long enough that a deleted state row can neither re-alert a row still inside the lookback window
        // nor lift the one-Threshold-per-quiet-period suppression.
        var retention = Max(Max(options.QuietPeriod, options.Lookback + options.Lease), MinimumRetention);
        await store.CleanupAsync(applicationName, retention, ct).ConfigureAwait(false);
    }

    private async Task<bool> RetryStaleBatchAsync(CancellationToken ct)
    {
        var stale = await store.FindStaleBatchAsync(applicationName, options.Lease, options.MaxAttempts, ct).ConfigureAwait(false);
        if (stale is null)
            return false;

        var batchId = Guid.NewGuid();
        if (!await store.TryReclaimAsync(applicationName, stale.Value, batchId, options.Lease, ct).ConfigureAwait(false))
            return false; // another instance took it over

        var rows = await store.GetClaimedAsync(applicationName, batchId, ct).ConfigureAwait(false);
        if (rows.Count == 0)
        {
            // The exception rows were purged while the batch waited; there is nothing left to send.
            await store.MarkFailedAsync(batchId, ct).ConfigureAwait(false);
            return true;
        }

        await SendAndMarkAsync(batchId, AlertDigestBuilder.Build(rows, applicationName, options), ct).ConfigureAwait(false);
        return true;
    }

    private async Task SendNewAlertsAsync(CancellationToken ct)
    {
        var fetched = await store.GetCandidatesAsync(
            applicationName, options, options.MaxGroupsPerMessage * CandidateFetchFactor, ct).ConfigureAwait(false);
        var candidates = OnePerException(fetched);
        if (candidates.Count == 0)
            return;

        // Claim only the groups that fit into the message; the rest stay unclaimed for the next run.
        var fit = AlertDigestBuilder.Build(candidates, applicationName, options).IncludedCount;
        var batchId = Guid.NewGuid();
        var won = new List<ExceptionAlertCandidate>();
        foreach (var candidate in candidates.Take(fit))
        {
            if (await store.TryClaimAsync(candidate, batchId, ct).ConfigureAwait(false))
                won.Add(candidate);
        }

        if (won.Count == 0)
            return;

        await SendAndMarkAsync(batchId, AlertDigestBuilder.Build(won, applicationName, options), ct).ConfigureAwait(false);
    }

    private async Task SendAndMarkAsync(Guid batchId, AlertDigest digest, CancellationToken ct)
    {
        switch (await channel.SendAsync(digest, ct).ConfigureAwait(false))
        {
            case AlertSendOutcome.Sent:
                await store.MarkSentAsync(batchId, ct).ConfigureAwait(false);
                break;
            case AlertSendOutcome.Permanent:
                await store.MarkFailedAsync(batchId, ct).ConfigureAwait(false);
                logger.LogError("Exception alert batch {BatchId} was rejected by the channel and will not be retried.", batchId);
                break;
            case AlertSendOutcome.Transient:
                // Left claimed: after the lease any instance retries it, up to MaxAttempts.
                logger.LogWarning("Exception alert batch {BatchId} could not be sent; it will be retried.", batchId);
                break;
            default:
                throw new InvalidOperationException("Unknown alert send outcome.");
        }
    }

    // A row that qualifies for several triggers is alerted once, under the lowest trigger value (New first).
    private static List<ExceptionAlertCandidate> OnePerException(IReadOnlyList<ExceptionAlertCandidate> candidates) =>
        candidates.GroupBy(c => c.ExceptionId)
            .Select(g => g.OrderBy(c => c.Trigger).First())
            .OrderBy(c => c.Trigger).ThenByDescending(c => c.LastSeenUtc)
            .ToList();

    private TimeSpan JitteredInterval()
    {
        var factor = 1 + ((Random.Shared.NextDouble() - 0.5) * IntervalJitter * 2);
        return TimeSpan.FromTicks((long)(options.Interval.Ticks * factor));
    }

    private static TimeSpan Max(TimeSpan a, TimeSpan b) => a >= b ? a : b;
}
