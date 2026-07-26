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
}
