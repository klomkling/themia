using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Themia.Scheduling;

/// <summary>
/// Warns at startup that a persistent scheduler is running unclustered — the one configuration in this
/// package that Quartz's own documentation prohibits, and the only one nothing else can catch.
/// </summary>
/// <remarks>
/// Quartz.NET's clustering documentation is explicit: <i>"Never start (scheduler.Start()) a non-clustered
/// instance against the same set of database tables that any other instance is running (Start()ed)
/// against. You may get serious data corruption, and will definitely experience erratic behavior."</i>
/// <para>
/// <b>Why a blanket warning rather than a check.</b> There is no way to detect it. An unclustered
/// scheduler never writes a <c>QRTZ_SCHEDULER_STATE</c> row at all — Quartz reaches
/// <c>InsertSchedulerState</c> only through <c>ClusterCheckIn</c>, which only the <c>ClusterManager</c>
/// drives, and that is constructed only when clustering is on. And <c>quartz.scheduler.instanceId</c>
/// defaults to the literal <c>NON_CLUSTERED</c>, so even if rows were written every node would collide on
/// one primary key instead of appearing separately. That is why the documentation states a prohibition
/// rather than Quartz enforcing one, and why this is a log line rather than a guard (coord #0071).
/// </para>
/// <para>
/// <b>Why it is worth one filtered line.</b> Every other hazard in this package is loud at the moment
/// someone causes it: an unsupported engine throws at migration time, a missing connection string throws
/// at registration. This one is reached by writing <em>no</em> configuration — persistence defaults on —
/// and only becomes real on the day someone scales out for an unrelated reason, months after the decision
/// that caused it. A note in a changelog is read by the person who upgrades, not by the person who scales.
/// </para>
/// </remarks>
internal sealed class UnclusteredPersistenceAdvisory(ILogger<UnclusteredPersistenceAdvisory> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogWarning(
            "Themia.Scheduling: the Quartz scheduler is persistent but NOT clustered. Running a second "
            + "instance against the same qrtz_* tables in this state is prohibited by Quartz — it causes "
            + "data corruption and erratic scheduling, not merely duplicated work. This is safe on a single "
            + "instance; if you scale out, enable clustering first. Nothing can detect the unsafe state at "
            + "runtime, which is why this warning is unconditional.");

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
