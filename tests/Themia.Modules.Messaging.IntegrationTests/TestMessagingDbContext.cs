using Microsoft.EntityFrameworkCore;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.EFCore;
using Themia.Modules.Messaging.EntityConfiguration;

namespace Themia.Modules.Messaging.IntegrationTests;

/// <summary>
/// A <see cref="ThemiaDbContext"/> that registers the Messaging model — the EF adopter pattern under
/// test (the adopter calls <c>modelBuilder.ApplyThemiaMessaging()</c> in their context).
/// </summary>
public sealed class TestMessagingDbContext(
    DbContextOptions options,
    ITenantContext? tenantContext = null)
    : ThemiaDbContext(options, tenantContext, null)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyThemiaMessaging();
        base.OnModelCreating(modelBuilder);
    }
}
