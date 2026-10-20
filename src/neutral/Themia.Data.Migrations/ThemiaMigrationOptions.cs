using Microsoft.Extensions.Logging;

namespace Themia.Data.Migrations;

/// <summary>
/// Optional settings for <see cref="ThemiaMigrations.Run(MigrationEngine, string, ThemiaMigrationOptions?, System.Reflection.Assembly[])"/>.
/// </summary>
public sealed class ThemiaMigrationOptions
{
    /// <summary>The default <see cref="LockTimeout"/> — long enough to outlast a large migration on the instance ahead.</summary>
    public static TimeSpan DefaultLockTimeout { get; } = TimeSpan.FromMinutes(15);

    /// <summary>
    /// How long to wait for the migration lock before giving up. Defaults to
    /// <see cref="DefaultLockTimeout"/>.
    /// </summary>
    /// <remarks>
    /// The wait is bounded rather than infinite so a wedged lock holder surfaces as a boot failure naming
    /// the lock, instead of a replica that blocks forever, logs nothing, and is eventually killed by its
    /// orchestrator's startup probe. Note that an orchestrator's probe budget is usually far shorter than
    /// this timeout, so <see cref="Logger"/> — not the timeout — is what makes a stalled boot diagnosable.
    /// </remarks>
    public TimeSpan LockTimeout { get; init; } = DefaultLockTimeout;

    /// <summary>
    /// Logger for migration-lock diagnostics: one message before a contended wait begins, and a warning if
    /// the lock turns out to have been lost while migrating. Optional, but without it a boot that stalls
    /// waiting for another instance leaves no trace.
    /// </summary>
    public ILogger? Logger { get; init; }

    /// <summary>The default <see cref="LockConnectRetries"/>.</summary>
    public const int DefaultLockConnectRetries = 3;

    /// <summary>
    /// How many times to retry opening the lock connection, or taking the lock on a session that dropped,
    /// after a transient failure (a dropped socket, a stream read error, a driver-flagged transient error).
    /// Defaults to <see cref="DefaultLockConnectRetries"/>; <c>0</c> disables retrying.
    /// </summary>
    /// <remarks>
    /// Retrying here is safe because nothing has been applied yet: the lock is taken before the first
    /// migration runs. A genuine lock wait timeout and any failure inside a migration are never retried.
    /// </remarks>
    public int LockConnectRetries { get; init; } = DefaultLockConnectRetries;

    /// <summary>
    /// Delay before the first retry; each further retry waits twice as long as the previous one.
    /// Defaults to one second.
    /// </summary>
    public TimeSpan LockConnectBackoff { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Test seam for the backoff delay.</summary>
    internal Action<TimeSpan> Sleep { get; init; } = Thread.Sleep;
}
