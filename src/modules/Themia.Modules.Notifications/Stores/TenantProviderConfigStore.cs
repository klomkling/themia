using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.UnitOfWork;
using Themia.Modules.Notifications.Entities;
using Themia.Modules.Notifications.Specifications;
using Themia.Notifications;

namespace Themia.Modules.Notifications.Stores;

/// <summary>Repository-backed <see cref="ITenantProviderConfigStore"/>. Peer-agnostic: the framework binds the
/// injected repository / unit of work to EF or Dapper. The repository stamps the tenant on insert and applies
/// the tenant + soft-delete query filters by construction — the store never re-filters by tenant.</summary>
internal sealed class TenantProviderConfigStore(
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
