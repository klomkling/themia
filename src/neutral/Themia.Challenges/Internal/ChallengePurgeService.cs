using Dapper;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Themia.Challenges.Internal;

/// <summary>
/// Background retention purge for the two tables <see cref="Migrations.ChallengeSchemaMigration"/>
/// creates. Deliberately two independent rules, mirroring the deliberately opposite lifetimes described
/// on <see cref="IChallengeDialect.PurgeExpiredSql"/> and <see cref="IChallengeDialect.PurgeElapsedWindowsSql"/>:
/// <c>challenges</c> rows are purged aggressively (every login attempt creates one) after
/// <see cref="ChallengeOptions.ChallengeRetentionHours"/>; <c>challenge_rate_windows</c> rows are purged
/// only once fully elapsed — <see cref="ChallengeOptions.WidestConfiguredWindow"/> plus a safety margin —
/// never on the challenge-row setting, because a rate-limit counter must outlive the challenges it
/// counted or an attacker simply waits for it to reset the per-key ceiling that bounds the SMS bill.
/// </summary>
/// <remarks>
/// <b>The purge-due gate advances only after a purge attempt actually succeeds.</b>
/// <c>Themia.Messaging</c>'s <c>OutboxDrainer</c> carries the same lesson learned the hard way: an
/// earlier version there advanced its equivalent gate before confirming the purge had succeeded, so a
/// single transient failure (a lock-wait timeout, say) silently suppressed retention for a whole
/// interval — nothing retried until the next scheduled run, and nothing in the logs distinguished that
/// from "nothing to purge". Here <see cref="PurgeIfDueAsync"/> sets <c>nextPurgeAt</c> only after
/// <see cref="PurgeOnceAsync"/> returns without throwing; a failure is caught, logged, and leaves the
/// gate untouched, so the very next poll tick (<see cref="PollInterval"/> later, not a full
/// <see cref="PurgeInterval"/> later) retries instead of waiting out the interval.
/// <para>
/// The dialect dependency is optional (defaults to <see langword="null"/> when nothing registers one),
/// the same shape <c>OutboxDrainer</c> uses for its purge dialect. This keeps host startup from throwing
/// a raw DI activation error if an adopter registers <see cref="DependencyInjection.ChallengeServiceCollectionExtensions.AddThemiaChallenges"/>
/// before an engine package: <see cref="IChallengeService"/> already owns the loud, named guard for a
/// missing dialect (checked at first resolution) — this service simply has nothing to purge yet and
/// waits for the next poll, by which point registration has normally completed.
/// </para>
/// </remarks>
internal sealed class ChallengePurgeService(
    ChallengeOptions options,
    TimeProvider time,
    ILogger<ChallengePurgeService> logger,
    IChallengeDialect? dialect = null) : BackgroundService
{
    /// <summary>How often the loop wakes to check whether a purge is due.</summary>
    private static readonly TimeSpan PollInterval = TimeSpan.FromHours(1);

    /// <summary>Minimum gap between two successful purge passes.</summary>
    private static readonly TimeSpan PurgeInterval = TimeSpan.FromHours(24);

    /// <summary>
    /// Added on top of the widest configured rate-limit window before purging
    /// <c>challenge_rate_windows</c> rows, so clock skew or a slightly late purge cycle can never purge a
    /// counter a window still technically depends on.
    /// </summary>
    private static readonly TimeSpan WindowSafetyMargin = TimeSpan.FromHours(1);

    private DateTimeOffset nextPurgeAt = DateTimeOffset.MinValue;

    /// <inheritdoc />
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await PurgeIfDueAsync(stoppingToken).ConfigureAwait(false);

            try
            {
                await Task.Delay(PollInterval, time, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break; // host stop — clean shutdown.
            }
        }
    }

    /// <summary>
    /// Runs one purge attempt if due. Internal (not private) so a test can invoke it directly and
    /// deterministically, without waiting on the loop's own <see cref="PollInterval"/> delay.
    /// </summary>
    /// <param name="ct">A token to cancel the attempt.</param>
    internal async Task PurgeIfDueAsync(CancellationToken ct)
    {
        if (!options.PurgeEnabled || dialect is null)
        {
            return;
        }

        var now = time.GetUtcNow();
        if (now < nextPurgeAt)
        {
            return;
        }

        try
        {
            var (expiredDeleted, windowsDeleted) = await PurgeOnceAsync(dialect, now, ct).ConfigureAwait(false);

            // Advance the gate only after the purge above returned without throwing — see the type's
            // remarks for why this ordering is load-bearing.
            nextPurgeAt = now.Add(PurgeInterval);

            if (expiredDeleted + windowsDeleted > 0)
            {
                // Counts only — never the rows themselves. A challenge row carries a secret hash and a
                // token hash; a window row is keyed by the same key a challenge targets. Neither belongs
                // in a log.
                logger.LogInformation(
                    "Challenge purge removed {ExpiredDeleted} expired challenge rows and {WindowsDeleted} elapsed rate-window rows.",
                    expiredDeleted,
                    windowsDeleted);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // host stop — let the loop observe cancellation, not a purge failure.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Challenge retention purge failed; will retry on the next poll.");
        }
    }

    private async Task<(int ExpiredDeleted, int WindowsDeleted)> PurgeOnceAsync(
        IChallengeDialect activeDialect, DateTimeOffset now, CancellationToken ct)
    {
        await using var connection = activeDialect.CreateConnection();
        await connection.OpenAsync(ct).ConfigureAwait(false);

        var expiredCutoff = now.AddHours(-options.ChallengeRetentionHours);
        var expiredDeleted = await connection.ExecuteAsync(new CommandDefinition(
            activeDialect.PurgeExpiredSql,
            new { OlderThan = expiredCutoff },
            cancellationToken: ct)).ConfigureAwait(false);

        // A window row must outlive every challenge it counted, so this never purges on
        // ChallengeRetentionHours — only once a window has fully elapsed. No purpose configured yet
        // means nothing to compute a safe cutoff from, so the windows table is left alone this cycle
        // rather than guessing.
        var windowsDeleted = 0;
        var widestWindow = options.WidestConfiguredWindow();
        if (widestWindow > TimeSpan.Zero)
        {
            var windowsCutoff = now - widestWindow - WindowSafetyMargin;
            windowsDeleted = await connection.ExecuteAsync(new CommandDefinition(
                activeDialect.PurgeElapsedWindowsSql,
                new { OlderThan = windowsCutoff },
                cancellationToken: ct)).ConfigureAwait(false);
        }

        return (expiredDeleted, windowsDeleted);
    }
}
