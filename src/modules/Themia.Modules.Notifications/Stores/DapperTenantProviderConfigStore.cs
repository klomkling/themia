using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.UnitOfWork;
using Themia.Modules.Notifications.Entities;
using Themia.Modules.Notifications.Specifications;
using Themia.Notifications;

namespace Themia.Modules.Notifications.Stores;

/// <summary>Dapper peer of <see cref="ITenantProviderConfigStore"/>. Reads go through the tenant-seeded
/// read repository; writes are staged on the repository and flushed via the unit of work.</summary>
internal sealed class DapperTenantProviderConfigStore(
    IRepository<TenantProviderConfig, Guid> configs,
    IUnitOfWork unitOfWork) : ITenantProviderConfigStore
{
    public async Task<TenantProviderConfig?> FindAsync(NotificationChannel channel, CancellationToken ct = default) =>
        await configs.FirstOrDefaultAsync(new TenantProviderConfigByChannelSpec(channel), ct).ConfigureAwait(false);

    public async Task UpsertAsync(TenantProviderConfig config, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        var existing = await configs
            .FirstOrDefaultAsync(new TenantProviderConfigByChannelSpec(config.Channel), ct)
            .ConfigureAwait(false);

        if (existing is null)
        {
            await configs.AddAsync(config, ct).ConfigureAwait(false);
        }
        else
        {
            existing.Host = config.Host;
            existing.Port = config.Port;
            existing.Username = config.Username;
            existing.Password = config.Password;
            existing.FromAddress = config.FromAddress;
            existing.UseSsl = config.UseSsl;
            configs.Update(existing);
        }

        await unitOfWork.SaveChangesAsync(ct).ConfigureAwait(false);
    }
}
