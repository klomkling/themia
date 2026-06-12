using Microsoft.AspNetCore.Http;

namespace Themia.Modules.Scheduling;

/// <summary>
/// Options for <see cref="SchedulingModule"/> controlling the dashboard mount path, the
/// authorization gate, and the scheduler name used to scope execution-history records.
/// </summary>
public sealed class SchedulingModuleOptions
{
    /// <summary>
    /// The virtual path the Quartz dashboard is mounted under. Defaults to <c>/jobs</c>.
    /// </summary>
    public string VirtualPathRoot { get; set; } = "/jobs";

    /// <summary>
    /// The scheduler name used by <see cref="EfExecutionHistoryStore"/> to scope history and stats
    /// rows. When <see cref="UsePersistentStore"/> is <see langword="true"/> (default) the module
    /// applies this name to the scheduler it owns, so the two always match; it only needs to match a
    /// host-configured scheduler name when <see cref="UsePersistentStore"/> is <see langword="false"/>.
    /// Defaults to <c>QuartzScheduler</c> (Quartz.NET's default).
    /// </summary>
    public string SchedulerName { get; set; } = "QuartzScheduler";

    /// <summary>
    /// Host-supplied authorization gate for every dashboard request; returns <see langword="true"/>
    /// to allow. When <see langword="null"/>, the module defaults to authenticated-only access —
    /// hosts SHOULD supply an admin check here, as the dashboard is platform-admin surface.
    /// </summary>
    public Func<HttpContext, Task<bool>>? Authorize { get; set; }

    /// <summary>
    /// When <see langword="true"/> (default), the module registers a persistent Quartz scheduler
    /// (AdoJobStore over the <c>quartz</c> schema, System.Text.Json serializer) and starts it via the
    /// Quartz hosted service. Set to <see langword="false"/> to register no scheduler — the host then
    /// supplies its own <c>IScheduler</c> (via <c>ThemiaQuartzOptions.Scheduler</c> or DI), as before.
    /// </summary>
    public bool UsePersistentStore { get; set; } = true;
}
