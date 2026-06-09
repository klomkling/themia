using Microsoft.EntityFrameworkCore;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.EFCore;
using Themia.Framework.Data.Dapper.Conformance;

namespace Themia.Framework.Data.Dapper.PostgreSql.IntegrationTests;

/// <summary>
/// EF Core context mapping <see cref="Widget"/> onto the SAME <c>widgets</c> table the Dapper provider uses.
/// The TenantId value converter and the tenant/soft-delete global query filters are configured by
/// <see cref="ThemiaDbContext.OnModelCreating"/> automatically for <see cref="ITenantEntity"/>/<c>ISoftDeletable</c>
/// types, so this only needs to pin the table name and column conventions.
/// </summary>
public sealed class WidgetDbContext : ThemiaDbContext
{
    /// <summary>Initializes the context with the given options and tenant.</summary>
    public WidgetDbContext(DbContextOptions<WidgetDbContext> options, ITenantContext? tenantContext)
        : base(options, tenantContext)
    {
    }

    /// <summary>The widgets set.</summary>
    public DbSet<Widget> Widgets => Set<Widget>();

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Widget>(builder =>
        {
            // Snake-case naming convention resolves the columns; the table name is pinned explicitly so it
            // matches the fixture-created `widgets` table regardless of the DbSet property name.
            builder.ToTable("widgets");
            builder.HasKey(w => w.Id);

            // Client-assigned Guid key (same as the Dapper path): never store-generated.
            builder.Property(w => w.Id).ValueGeneratedNever();
            builder.Property(w => w.Name).IsRequired();
            builder.Property(w => w.TenantId);

            // The entity carries domain-event machinery from Entity<TId>; those are not persisted.
            builder.Ignore(w => w.DomainEvents);
        });

        base.OnModelCreating(modelBuilder);
    }
}
