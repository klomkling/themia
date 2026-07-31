using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using Themia.Messaging.Inbox;
using Themia.Messaging.Outbox;

namespace Themia.Modules.Messaging.Inbox;

/// <summary>
/// Deletes expired inbox admission records on a slow cadence. A background service rather than a
/// scheduled job so the module does not force a scheduler dependency on every adopter.
/// </summary>
internal sealed class InboxPurgeService(
    IInboxPurgeDialect purgeDialect,
    IOutboxDialect<ClaimedMessageRow> connectionSource,
    MessagingModuleOptions options,
    TimeProvider time,
    ILogger<InboxPurgeService> logger) : BackgroundService
{
    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (options.PurgeEnabled)
                {
                    await PurgeAsync(stoppingToken).ConfigureAwait(false);
                }

                await Task.Delay(TimeSpan.FromHours(24), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // host stop — clean shutdown.
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Inbox purge cycle failed; retrying on the next interval.");
                try
                {
                    await Task.Delay(TimeSpan.FromHours(1), stoppingToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    private async Task PurgeAsync(CancellationToken ct)
    {
        var cutoff = time.GetUtcNow().AddDays(-options.InboxRetentionDays);

        await using var connection = connectionSource.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);

        var total = 0;
        int deleted;
        do
        {
            ct.ThrowIfCancellationRequested();
            deleted = await purgeDialect.PurgeAdmittedAsync(connection, cutoff, 1000, ct).ConfigureAwait(false);
            total += deleted;
        }
        while (deleted == 1000);

        if (total > 0)
        {
            logger.LogInformation("Inbox purge removed {Deleted} admission records.", total);
        }
    }
}
