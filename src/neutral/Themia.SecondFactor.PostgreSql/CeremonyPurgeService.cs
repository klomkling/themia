using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Themia.SecondFactor.PostgreSql;

/// <summary>Deletes expired WebAuthn ceremonies hourly; unauthenticated begin calls would otherwise grow the table.</summary>
internal sealed class CeremonyPurgeService(IMfaCeremonyStore ceremonies, ILogger<CeremonyPurgeService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                var purged = await ceremonies.PurgeExpiredAsync(stoppingToken).ConfigureAwait(false);
                logger.LogDebug("Purged {Count} expired second-factor ceremonies.", purged);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A failed purge must not stop the loop; the next tick retries.
                logger.LogWarning(ex, "Purging expired second-factor ceremonies failed.");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false));
    }
}
