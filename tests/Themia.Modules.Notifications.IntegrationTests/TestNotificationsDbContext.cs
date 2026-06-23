using Microsoft.EntityFrameworkCore;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.EFCore;
using Themia.Modules.Notifications.EntityConfiguration;

namespace Themia.Modules.Notifications.IntegrationTests;

/// <summary>
/// A <see cref="ThemiaDbContext"/> that registers the Notifications model — the EF adopter pattern
/// under test (the adopter calls <c>modelBuilder.ApplyThemiaNotifications()</c> in their context).
/// </summary>
public sealed class TestNotificationsDbContext(
    DbContextOptions options,
    ITenantContext? tenantContext = null)
    : ThemiaDbContext(options, tenantContext, null)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyThemiaNotifications();
        base.OnModelCreating(modelBuilder);
    }
}
