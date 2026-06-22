using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.UnitOfWork;
using Themia.Modules.Notifications.Entities;
using Themia.Modules.Notifications.Specifications;

namespace Themia.Modules.Notifications.Stores;

/// <summary>Dapper peer of <see cref="IInAppNotificationStore"/>. Reads go through the tenant-seeded
/// read repository (the framework applies the tenant predicate + soft-delete filter by construction);
/// writes are staged on the repository and flushed via the unit of work.</summary>
internal sealed class DapperInAppNotificationStore(
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
