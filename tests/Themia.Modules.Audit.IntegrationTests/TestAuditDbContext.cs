using Microsoft.EntityFrameworkCore;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.EFCore;

namespace Themia.Modules.Audit.IntegrationTests;

/// <summary>
/// A bare <see cref="ThemiaDbContext"/> with no mapped entities. It exists only to give
/// <see cref="Themia.Framework.Data.EFCore.UnitOfWork.EfUnitOfWork"/> a real transaction to open — the
/// audit row itself is written through raw ADO via <see cref="Themia.Audit.IAuditStore"/>, not through
/// EF's change tracker, so no entity needs mapping here.
/// </summary>
public sealed class TestAuditDbContext(DbContextOptions options, ITenantContext? tenantContext = null)
    : ThemiaDbContext(options, tenantContext, null);
