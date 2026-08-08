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

    /// <summary>
    /// The Quartz instance id. Defaults to <c>AUTO</c>, which generates a unique id per node.
    /// </summary>
    /// <remarks>
    /// Leave this alone unless you have a reason. Under clustering every node must hold a
    /// <b>different</b> id, and two nodes sharing one corrupt scheduler state rather than failing
    /// cleanly — <c>AUTO</c> makes that unrepresentable. Themia cannot detect a duplicate across
    /// processes, so setting this explicitly with <see cref="UseClustering"/> on produces a startup
    /// warning: the configuration that permits the fault is the most that is observable from inside one
    /// process.
    /// </remarks>
    public string InstanceId { get; set; } = "AUTO";

    /// <summary>
    /// Whether this scheduler participates in a Quartz cluster. <see langword="false"/> by default.
    /// </summary>
    /// <remarks>
    /// <b>Required the moment more than one instance runs against the same tables.</b> Quartz's own
    /// documentation: <i>"Never start a non-clustered instance against the same set of database tables
    /// that any other instance is running against. You may get serious data corruption, and will
    /// definitely experience erratic behavior."</i> Clustering is what makes one node fire each trigger
    /// instead of every node firing it.
    /// <para>
    /// Off by default because turning it on for a single-instance host adds lock contention for no
    /// benefit — and defaulting it on would have added that contention to every existing adopter on
    /// upgrade, with no diff on their side and nothing failing.
    /// </para>
    /// <para>
    /// <b>Clocks must agree to within one second.</b> Quartz's documentation requires a time-sync
    /// daemon across clustered nodes; drift presents as misfires, which read as application bugs rather
    /// than as a scheduling problem. And a node pinned at 100% CPU may fail to update the job store,
    /// at which point other nodes consider its jobs lost and re-run them — so CPU starvation presents
    /// as duplicate execution.
    /// </para>
    /// <para>
    /// <b>Clustering protects Quartz's trigger bookkeeping, not your job's work.</b> A job that must not
    /// run twice for reasons of its own — a crawl against a rate-limited API, say — still needs its own
    /// lock, because clustering guarantees one <em>fire</em> and says nothing about what the job does
    /// with it. Persistence solves neither. (propertiezy, coord #0071.)
    /// </para>
    /// </remarks>
    public bool UseClustering { get; set; }

    /// <summary>
    /// When <see langword="true"/>, execution history is written to the <c>scheduling</c> schema instead
    /// of being held in memory. <see langword="false"/> by default.
    /// </summary>
    /// <remarks>
    /// Off by default so adopting this package does not silently start writing rows to a schema an
    /// existing host never asked for — <c>AddThemiaQuartz</c>'s in-memory store stays in place unless you
    /// say otherwise, and <c>/admin/jobs</c> keeps behaving exactly as it does today.
    /// <para>
    /// Persisting the scheduler and persisting its history are separate decisions: AdoJobStore keeps
    /// triggers across a restart and never touched history, which is why a host can have restart-surviving
    /// schedules and a dashboard that still forgets.
    /// </para>
    /// </remarks>
    public bool UsePersistentExecutionHistory { get; set; }
}
