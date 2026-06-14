using Microsoft.EntityFrameworkCore;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Auditing;
using Themia.Framework.Data.EFCore;
using Themia.Modules.Identity.EntityConfiguration;

namespace Themia.Modules.Identity.IntegrationTests;

/// <summary>
/// A ThemiaDbContext that registers the Identity model and resolves the audit user from
/// the ambient <see cref="ICurrentUserAccessor"/> — the EF adopter pattern under test.
/// </summary>
public sealed class TestIdentityDbContext(
    DbContextOptions options,
    ITenantContext? tenantContext = null,
    ICurrentUserAccessor? currentUserAccessor = null)
    : ThemiaDbContext(options, tenantContext, null)
{
    protected override string? CurrentUserId => currentUserAccessor?.UserId;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyThemiaIdentity();
        base.OnModelCreating(modelBuilder);
    }
}
