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
    /// rows. Must match the host's configured Quartz scheduler name. Defaults to
    /// <c>QuartzScheduler</c> (Quartz.NET's default).
    /// </summary>
    public string SchedulerName { get; set; } = "QuartzScheduler";

    /// <summary>
    /// Host-supplied authorization gate for every dashboard request; returns <see langword="true"/>
    /// to allow. When <see langword="null"/>, the module defaults to authenticated-only access —
    /// hosts SHOULD supply an admin check here, as the dashboard is platform-admin surface.
    /// </summary>
    public Func<HttpContext, Task<bool>>? Authorize { get; set; }
}
