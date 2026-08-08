using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Themia.Scheduling;

/// <summary>
/// Warns that a clustered scheduler is running with an explicitly-set instance id rather than
/// <c>AUTO</c>.
/// </summary>
/// <remarks>
/// Quartz requires every node in a cluster to hold a different instance id, and two nodes sharing one
/// corrupt scheduler state rather than failing cleanly. <c>AUTO</c> makes that unrepresentable; setting
/// the id by hand makes it a deployment detail somebody has to get right on every node, forever.
/// <para>
/// No process can observe another process's instance id, so the duplicate itself is undetectable from
/// here. The configuration that permits it is detectable, and that is what this names — the same
/// reasoning as <see cref="UnclusteredPersistenceAdvisory"/>, applied to the hazard one step along.
/// </para>
/// </remarks>
internal sealed class ExplicitInstanceIdAdvisory(
    SchedulingOptions options,
    ILogger<ExplicitInstanceIdAdvisory> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        logger.LogWarning(
            "Themia.Scheduling: clustering is enabled with an explicit InstanceId ('{InstanceId}') rather "
            + "than AUTO. Every node in a Quartz cluster must hold a DIFFERENT id; two nodes sharing one "
            + "corrupt scheduler state rather than failing cleanly, and no node can detect the collision. "
            + "Use AUTO unless something outside Themia requires otherwise.",
            options.InstanceId);

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
