using Microsoft.Extensions.DependencyInjection;

using Themia.Messaging.Outbox;
using Themia.Notifications;

namespace Themia.Modules.Notifications.Outbox;

/// <summary>
/// Delivers a claimed notification row through the registered channel sender. This is the notifications
/// half of the shared drainer: the drainer owns claiming, leasing and backoff; this owns what "send"
/// means and which failures are worth retrying.
/// </summary>
internal sealed class NotificationOutboxDispatcher : IOutboxDispatcher<ClaimedOutboxRow>
{
    /// <inheritdoc />
    public async Task<DispatchResult> DispatchAsync(IServiceProvider scopedServices, ClaimedOutboxRow row, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(scopedServices);
        ArgumentNullException.ThrowIfNull(row);

        try
        {
            var message = new NotificationMessage
            {
                Channel = row.Channel,
                Recipient = row.Recipient,
                Subject = row.Subject,
                Body = row.Body, // already rendered at enqueue
            };

            // forward-note: per-tenant sender/provider-config resolution is deferred — v1 resolves the global
            // sender here. WHEN a tenant-aware sender is wired, this must set the ambient tenant for the
            // row (row.TenantId) BEFORE resolving config, else IProviderConfigResolver resolves with a null
            // tenant and silently falls back to the global config.
            var result = await SendAsync(scopedServices, message, ct).ConfigureAwait(false);

            // Exhaustive over NotificationOutcome deliberately: a new outcome must break this build rather
            // than fall into a default and be silently mishandled. That is exactly how NotConfigured was
            // once treated as Transient, which retried every notification to the attempt cap and then
            // dead-lettered it on any host running without a configured provider.
            return result.Outcome switch
            {
                NotificationOutcome.Sent => DispatchResult.Delivered(),

                // Permanent, not Transient: "no provider is configured" is a deployment-level state, not a
                // property of this message. Configuration cannot change between backoff attempts, so
                // retrying burns the attempt cap to reach the same dead-letter with five times the log
                // noise. Failing on the first attempt puts the reason in last_error immediately, where an
                // operator can see it.
                NotificationOutcome.NotConfigured => DispatchResult.Permanent(
                    result.Error ?? "No provider is configured for this channel; nothing was sent."),

                NotificationOutcome.Failed => DispatchResult.Transient(result.Error ?? "Sender reported failure."),

                _ => throw new NotSupportedException($"Unhandled notification outcome {result.Outcome}."),
            };
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // host stop — not a delivery failure.
        }
        catch (Exception ex) when (ex is FormatException or NotSupportedException)
        {
            // A malformed address/body (FormatException) or an undeliverable channel routed to the outbox
            // (NotSupportedException) is permanent — retrying cannot help, so dead-letter immediately.
            return DispatchResult.Permanent(ex.Message, ex);
        }
    }

    // Direct switch: await the right sender inline. (No SenderAdapter indirection — the plan prefers this.)
    private static async Task<NotificationResult> SendAsync(IServiceProvider sp, NotificationMessage message, CancellationToken ct) =>
        message.Channel switch
        {
            NotificationChannel.Email => await sp.GetRequiredService<IEmailSender>().SendAsync(message, ct).ConfigureAwait(false),
            NotificationChannel.Sms => await sp.GetRequiredService<ISmsSender>().SendAsync(message, ct).ConfigureAwait(false),
            NotificationChannel.Push => await sp.GetRequiredService<IPushSender>().SendAsync(message, ct).ConfigureAwait(false),
            _ => throw new NotSupportedException($"Channel {message.Channel} is not deliverable via the outbox."),
        };
}
