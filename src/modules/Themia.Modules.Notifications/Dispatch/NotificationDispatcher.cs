using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Modules.Notifications.Entities;
using Themia.Modules.Notifications.Outbox;
using Themia.Notifications;

namespace Themia.Modules.Notifications.Dispatch;

internal sealed class NotificationDispatcher(
    IPreferenceResolver preferences,
    IOutboxStore outbox,
    IRepository<InAppNotification, Guid> inAppRepository,
    INotificationTemplateRenderer renderer,
    ITenantContext tenantContext,
    TimeProvider time) : INotificationDispatcher
{
    public async Task DispatchAsync(NotificationRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var resolved = await preferences.ResolveAsync(request.UserId, request.Channels, ct).ConfigureAwait(false);
        var body = request.Body ?? (request.Template is null ? string.Empty : renderer.Render(request.Template, request.Model ?? new object()));
        var now = time.GetUtcNow();
        var tenant = tenantContext.CurrentTenantId;

        foreach (var channel in resolved.EnabledChannels)
        {
            if (channel == NotificationChannel.InApp)
            {
                var notification = new InAppNotification
                {
                    TenantId = tenant,
                    UserId = request.UserId,
                    Title = request.Subject ?? string.Empty,
                    Body = body,
                    CreatedAt = now,
                };
                notification.SetId(Guid.NewGuid()); // framework: Entity<TId>.Id has a protected setter; use SetId
                // Stage-only (no SaveChanges) so in-app commits atomically with the caller's UoW — same contract as the outbox.
                await inAppRepository.AddAsync(notification, ct).ConfigureAwait(false);
                continue;
            }

            var recipient = request.Recipients?.GetValueOrDefault(channel);
            if (string.IsNullOrWhiteSpace(recipient)) continue; // no address for this channel
            var message = new OutboxMessage
            {
                TenantId = tenant,
                Channel = channel,
                Recipient = recipient,
                Subject = request.Subject,
                Body = body,
                Status = OutboxStatus.Pending,
                Attempts = 0,
                NextAttemptAt = request.ScheduledFor ?? now,
                ScheduledFor = request.ScheduledFor,
                CreatedAt = now,
            };
            message.SetId(Guid.NewGuid());
            await outbox.EnqueueAsync(message, ct).ConfigureAwait(false);
        }
    }
}
