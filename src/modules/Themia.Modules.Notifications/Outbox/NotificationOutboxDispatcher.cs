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

            return result.Succeeded
                ? DispatchResult.Delivered()
                : DispatchResult.Transient(result.Error ?? "Sender reported failure.");
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
