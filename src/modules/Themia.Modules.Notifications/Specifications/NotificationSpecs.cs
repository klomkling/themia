using Themia.Framework.Data.Abstractions.Specifications;
using Themia.Modules.Notifications.Entities;
using Themia.Notifications;

namespace Themia.Modules.Notifications.Specifications;

/// <summary>A user's in-app notifications, newest first. The framework tenant filter scopes it to the
/// current tenant; soft-deleted rows are excluded by the framework's soft-delete filter.</summary>
internal sealed class InAppNotificationsForUserSpec : Specification<InAppNotification>
{
    public InAppNotificationsForUserSpec(string userId, bool unreadOnly)
    {
        // Build the predicate by branch (not with a captured boolean inside the expression tree): the
        // Dapper specification translator only supports entity-column predicates.
        if (unreadOnly)
        {
            Where(x => x.UserId == userId && !x.IsRead);
        }
        else
        {
            Where(x => x.UserId == userId);
        }

        OrderByDescending(x => x.CreatedAt);
    }
}

/// <summary>The current tenant's notification preferences, optionally filtered to a single user.</summary>
internal sealed class NotificationPreferencesSpec : Specification<NotificationPreference>
{
    public NotificationPreferencesSpec(string? userId)
    {
        if (userId is not null)
        {
            Where(x => x.UserId == userId);
        }
    }
}

/// <summary>The current tenant's preference for a specific (user, channel) pair, used to upsert.</summary>
internal sealed class NotificationPreferenceByUserChannelSpec : Specification<NotificationPreference>
{
    public NotificationPreferenceByUserChannelSpec(string? userId, NotificationChannel channel) =>
        Where(x => x.UserId == userId && x.Channel == channel);
}

/// <summary>The current tenant's provider configuration for a channel.</summary>
internal sealed class TenantProviderConfigByChannelSpec : Specification<TenantProviderConfig>
{
    public TenantProviderConfigByChannelSpec(NotificationChannel channel) => Where(x => x.Channel == channel);
}
