namespace Themia.Scheduling;

/// <summary>
/// Options for the persistent Quartz scheduler registered by
/// <see cref="DependencyInjection.SchedulingServiceCollectionExtensions.AddThemiaScheduling"/>.
/// </summary>
public sealed class SchedulingOptions
{
    /// <summary>
    /// The scheduler name. Scopes execution-history and stats rows, and — once clustering exists —
    /// identifies which nodes belong to the same cluster. Defaults to Quartz.NET's own default.
    /// </summary>
    public string SchedulerName { get; set; } = "QuartzScheduler";

    /// <summary>
    /// When <see langword="true"/> (default), a persistent Quartz scheduler is registered over
    /// AdoJobStore and started by the Quartz hosted service. Set to <see langword="false"/> to register
    /// no scheduler at all — the host then supplies its own <c>IScheduler</c>.
    /// </summary>
    /// <remarks>
    /// Persistence is what survives a restart: an in-memory scheduler restarts every interval trigger
    /// from the moment the process came up, so a schedule silently drifts on every deploy, and misfires
    /// are not recoverable because nothing recorded that the fire was missed.
    /// </remarks>
    public bool UsePersistentStore { get; set; } = true;
}
