using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.UnitOfWork;
using Themia.Modules.Notifications.Entities;
using Themia.Modules.Notifications.Specifications;

namespace Themia.Modules.Notifications.Stores;

/// <summary>EF Core peer of <see cref="IInAppNotificationStore"/>. Reads and writes go through the
/// framework's tenant-aware repository / unit of work, which stamps the tenant on insert and applies the
/// tenant + soft-delete query filters by construction — the store never re-filters by tenant.</summary>
internal sealed class EfInAppNotificationStore(
    IRepository<InAppNotification, Guid> notifications,
    IUnitOfWork unitOfWork) : IInAppNotificationStore
{
    public async Task AddAsync(InAppNotification notification, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(notification);
        await notifications.AddAsync(notification, ct).ConfigureAwait(false);
        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<InAppNotification>> ListForUserAsync(string userId, bool unreadOnly, CancellationToken ct = default) =>
        await notifications.ListAsync(new InAppNotificationsForUserSpec(userId, unreadOnly), ct).ConfigureAwait(false);

    public async Task<bool> MarkReadAsync(Guid id, CancellationToken ct = default)
    {
        var entity = await notifications.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (entity is null)
        {
            return false;
        }

        entity.IsRead = true;
        entity.ReadAt = DateTimeOffset.UtcNow;
        notifications.Update(entity);
        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
        return true;
    }
}
