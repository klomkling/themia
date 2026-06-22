using System.Data.Common;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Themia.Notifications;

namespace Themia.Modules.Notifications.Outbox;

/// <summary>
/// Background service that drains the transactional outbox: it claims due rows under a lease,
/// dispatches each to the registered channel sender, and marks the row sent or failed (with
/// backoff). It owns the delivery outcome — failures are recorded on the row, not rethrown.
/// </summary>
internal sealed class OutboxDrainer(
    INotificationsSqlDialect dialect,
    DrainSignal signal,
    IServiceScopeFactory scopeFactory,
    NotificationsModuleOptions options,
    TimeProvider time,
    ILogger<OutboxDrainer> logger) : BackgroundService
{
    private const int MaxErrorLength = 1000;

    private readonly string leaseOwner = $"{Environment.MachineName}:{Environment.ProcessId}";

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
    internal async Task<int> DrainOnceAsync(CancellationToken ct)
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
            return 0;
        }

        using var scope = scopeFactory.CreateScope();
        foreach (var row in claimed)
        {
            ct.ThrowIfCancellationRequested();
            await DeliverAsync(scope.ServiceProvider, connection, row, ct).ConfigureAwait(false);
        }

        return claimed.Count;
    }

    private async Task DeliverAsync(IServiceProvider sp, DbConnection connection, ClaimedOutboxRow row, CancellationToken ct)
    {
        try
        {
            var message = new NotificationMessage
            {
                Channel = row.Channel,
                Recipient = row.Recipient,
                Subject = row.Subject,
                Body = row.Body, // already rendered at enqueue
            };

            var result = await SendAsync(sp, message, ct).ConfigureAwait(false);
            if (!result.Succeeded)
            {
                throw new InvalidOperationException(result.Error ?? "Sender reported failure.");
            }

            await dialect.CompleteAsync(connection, row.Id, time.GetUtcNow(), ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // host stop — let the cycle observe cancellation, do not record as a failure.
        }
        catch (FormatException ex)
        {
            // A malformed address/body is permanent — retrying cannot help, so dead-letter immediately.
            await FailRowAsync(connection, row, permanent: true, ex, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            await FailRowAsync(connection, row, permanent: false, ex, ct).ConfigureAwait(false);
        }
    }

    // Direct switch: await the right sender inline. (No SenderAdapter indirection — the plan prefers this.)
    private static async Task<NotificationResult> SendAsync(IServiceProvider sp, NotificationMessage message, CancellationToken ct) =>
        message.Channel switch
        {
            NotificationChannel.Email => await sp.GetRequiredService<IEmailSender>().SendAsync(message, ct).ConfigureAwait(false),
            NotificationChannel.Sms => await sp.GetRequiredService<ISmsSender>().SendAsync(message, ct).ConfigureAwait(false),
            NotificationChannel.Push => await sp.GetRequiredService<IPushSender>().SendAsync(message, ct).ConfigureAwait(false),
            _ => throw new NotSupportedException($"Channel {message.Channel} is not deliverable via the outbox."),
        };

    private async Task FailRowAsync(DbConnection connection, ClaimedOutboxRow row, bool permanent, Exception ex, CancellationToken ct)
    {
        var attempts = row.Attempts + 1;
        var dead = permanent || BackoffPolicy.IsDead(attempts, options.MaxAttempts);
        var next = BackoffPolicy.NextAttemptAt(time.GetUtcNow(), attempts, options.MaxAttempts);

        // Log once, with safe context only (no recipient PII, no credentials). The drainer owns the
        // outcome (THEMIA101: no log-and-rethrow) — record it on the row instead of propagating.
        logger.LogWarning(
            ex,
            "Notification {Id} on {Channel} failed (attempt {Attempts}); {Outcome}.",
            row.Id,
            row.Channel,
            attempts,
            dead ? "dead-lettered" : "will retry");

        await dialect.FailAsync(connection, row.Id, attempts, next, dead, Truncate(ex.Message, MaxErrorLength), ct).ConfigureAwait(false);
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max];
}
