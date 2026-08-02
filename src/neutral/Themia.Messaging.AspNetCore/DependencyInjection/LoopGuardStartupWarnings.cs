using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Themia.Messaging.AspNetCore.DependencyInjection;

/// <summary>
/// Logs a startup warning for every peer declared bi-directional (<see cref="VerificationOptions.MarkBiDirectional"/>)
/// whose requests carry no <c>Origin</c> header — on that channel the loop guard cannot run at all, which
/// is a gap, not a degradation, and worth surfacing at boot rather than inferring from a cycling message.
/// </summary>
internal sealed class LoopGuardStartupWarnings(
    VerificationOptions verificationOptions, ILogger<LoopGuardStartupWarnings> logger) : IHostedService
{
    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken)
    {
        foreach (var (peerName, sendsOriginHeader) in verificationOptions.BiDirectionalPeers)
        {
            if (sendsOriginHeader)
            {
                continue;
            }

            logger.LogWarning(
                "Peer '{Peer}' is configured for bi-directional messaging but its requests carry no Origin "
                + "header, so the loop guard cannot run on this channel. Loop protection is absent, not "
                + "merely degraded, until both ends verify with the framework.",
                peerName);
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
