using Microsoft.EntityFrameworkCore;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Auditing;
using Themia.Framework.Data.EFCore;
using Themia.Modules.Storage.EntityConfiguration;

namespace Themia.Modules.Storage.IntegrationTests;

/// <summary>
/// A ThemiaDbContext that registers the Storage model and resolves the audit user from
/// the ambient <see cref="ICurrentUserAccessor"/> — the EF adopter pattern under test.
/// </summary>
public sealed class TestStorageDbContext(
    DbContextOptions options,
    ITenantContext? tenantContext = null,
    ICurrentUserAccessor? currentUserAccessor = null)
    : ThemiaDbContext(options, tenantContext, null)
{
    protected override string? CurrentUserId => currentUserAccessor?.UserId;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyThemiaStorage();
        base.OnModelCreating(modelBuilder);
    }
}
