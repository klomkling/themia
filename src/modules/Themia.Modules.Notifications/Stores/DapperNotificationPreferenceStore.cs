using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.UnitOfWork;
using Themia.Modules.Notifications.Entities;
using Themia.Modules.Notifications.Specifications;

namespace Themia.Modules.Notifications.Stores;

/// <summary>Dapper peer of <see cref="INotificationPreferenceStore"/>. Reads go through the tenant-seeded
/// read repository; writes are staged on the repository and flushed via the unit of work.</summary>
internal sealed class DapperNotificationPreferenceStore(
    IRepository<NotificationPreference, Guid> preferences,
    IUnitOfWork unitOfWork) : INotificationPreferenceStore
{
    public async Task<IReadOnlyList<NotificationPreference>> ListAsync(string? userId, CancellationToken ct = default) =>
        await preferences.ListAsync(new NotificationPreferencesSpec(userId), ct).ConfigureAwait(false);

    public async Task UpsertAsync(NotificationPreference preference, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(preference);
        var existing = await preferences
            .FirstOrDefaultAsync(new NotificationPreferenceByUserChannelSpec(preference.UserId, preference.Channel), ct)
            .ConfigureAwait(false);

        if (existing is null)
        {
            await preferences.AddAsync(preference, ct).ConfigureAwait(false);
        }
        else
        {
            existing.IsEnabled = preference.IsEnabled;
            existing.Locale = preference.Locale;
            preferences.Update(existing);
        }

        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
