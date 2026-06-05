using System.Linq.Expressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using Themia.Framework.Core.Abstractions.Entities;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.EFCore.Infrastructure;

namespace Themia.Framework.Data.EFCore;

/// <summary>
/// Base EF Core <see cref="DbContext"/> applying Themia conventions such as tenant-aware query filters.
/// </summary>
public abstract class ThemiaDbContext : DbContext
{
    private static readonly ValueConverter<TenantId, string> TenantIdConverter =
        new(id => id.Value, value => new TenantId(value));

    private static readonly ValueComparer<TenantId> TenantIdComparer =
        new(
            (left, right) => AreTenantIdsEqual(left, right),
            id => GetTenantIdHashCode(id),
            id => string.IsNullOrEmpty(id.Value) ? default : new TenantId(id.Value));

    private static readonly ValueConverter<TenantId?, string?> NullableTenantIdConverter =
        new(
            id => id.HasValue ? id.Value.Value : null,
            value => value == null ? null : new TenantId(value));

    private static readonly ValueComparer<TenantId?> NullableTenantIdComparer =
        new(
            (left, right) =>
                (!left.HasValue && !right.HasValue) ||
                (left.HasValue && right.HasValue && AreTenantIdsEqual(left.Value, right.Value)),
            id => id.HasValue ? GetTenantIdHashCode(id.Value) : 0,
            id => id.HasValue
                ? string.IsNullOrEmpty(id.Value.Value)
                    ? null
                    : new TenantId(id.Value.Value)
                : null);

    private readonly ITenantContext? tenantContext;
    private readonly TimeProvider timeProvider;
    private readonly TenantId? previousTenantId;
    private readonly bool tenantContextApplied;

    /// <summary>
    /// Initializes a new instance of the <see cref="ThemiaDbContext"/> class.
    /// </summary>
    /// <param name="options">Database context options.</param>
    /// <param name="tenantContext">Optional tenant context used for query filters.</param>
    /// <param name="timeProvider">Time provider for audit timestamps. Defaults to system time.</param>
    protected ThemiaDbContext(
        DbContextOptions options,
        ITenantContext? tenantContext = null,
        TimeProvider? timeProvider = null)
        : base(options)
    {
        this.tenantContext = tenantContext;
        this.timeProvider = timeProvider ?? TimeProvider.System;
        previousTenantId = TenantContextAccessor.CurrentTenantId;

        // Set ambient tenant context for RuntimeTenantAccess strategy
        // For PerTenantModel strategy, this is not used (tenant is baked into model)
        if (TenantIsolationStrategy == TenantIsolationStrategy.RuntimeTenantAccess && tenantContext is not null)
        {
            TenantContextAccessor.CurrentTenantId = tenantContext?.CurrentTenantId;
            tenantContextApplied = true;
        }
    }

    /// <summary>
    /// Gets the current tenant identifier when available.
    /// </summary>
    protected TenantId? CurrentTenantId => tenantContext?.CurrentTenantId;

    /// <summary>
    /// Gets the tenant identifier that the runtime query filter actually evaluates against, by strategy.
    /// Find must read the same source as <see cref="GetCurrentTenantExpression"/> so they can never disagree.
    /// </summary>
    /// <remarks>
    /// <list type="bullet">
    /// <item><term>RuntimeTenantAccess</term><description>Reads <see cref="TenantContextAccessor.CurrentTenantId"/> — identical to the runtime filter expression.</description></item>
    /// <item><term>PerTenantModel</term><description>Reads the injected <see cref="CurrentTenantId"/> — the filter bakes this value as a constant at model-build time.</description></item>
    /// </list>
    /// </remarks>
    private TenantId? EffectiveFilterTenantId =>
        TenantIsolationStrategy == TenantIsolationStrategy.RuntimeTenantAccess
            ? TenantContextAccessor.CurrentTenantId   // same source as the runtime filter
            : CurrentTenantId;                          // PerTenantModel: injected/constant (filter bakes a constant of this)

    /// <summary>
    /// Gets the injected tenant context, when provided.
    /// </summary>
    protected ITenantContext? TenantContext => tenantContext;

    /// <summary>
    /// Gets the injected tenant context (internal for model cache key factory).
    /// </summary>
    internal ITenantContext? InternalTenantContext => tenantContext;

    /// <summary>
    /// Indicates whether records without a tenant identifier are included for tenant-scoped queries.
    /// </summary>
    protected virtual bool IncludeGlobalRecordsForTenants => true;

    /// <summary>
    /// Indicates whether tenant query filters should be applied automatically.
    /// </summary>
    protected virtual bool EnableTenantFilters => true;

    /// <summary>
    /// Indicates whether soft delete query filters should be applied automatically.
    /// </summary>
    protected virtual bool EnableSoftDeleteFilters => true;

    /// <summary>
    /// Determines the tenant isolation strategy.
    /// </summary>
    /// <remarks>
    /// <para><b>RuntimeTenantAccess</b> (default): All tenants share one compiled model with runtime tenant resolution.
    /// Best for SaaS applications with many tenants. Uses constant memory, compatible with all scenarios.</para>
    ///
    /// <para><b>PerTenantModel</b>: Each tenant gets its own compiled model. Best for applications with &lt; 100 tenants.
    /// Provides best performance per-tenant but consumes more memory (~500KB per tenant).
    /// IMPORTANT: Cannot be used with UseInternalServiceProvider. Override this property to opt-in.</para>
    /// </remarks>
    protected virtual TenantIsolationStrategy TenantIsolationStrategy => TenantIsolationStrategy.RuntimeTenantAccess;

    /// <summary>
    /// Gets the current user identifier for audit purposes. Override to provide custom user resolution.
    /// </summary>
    protected virtual string? CurrentUserId => null;

    /// <inheritdoc />
    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        UpdateAuditableEntities();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    /// <inheritdoc />
    public override async Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        UpdateAuditableEntities();
        return await base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <inheritdoc />
    public override object? Find(Type entityType, params object?[]? keyValues)
    {
        var entity = base.Find(entityType, keyValues);
        return ValidateTenantAccess(entity, entityType);
    }

    /// <inheritdoc />
    public override async ValueTask<object?> FindAsync(Type entityType, params object?[]? keyValues)
    {
        var entity = await base.FindAsync(entityType, keyValues);
        return ValidateTenantAccess(entity, entityType);
    }

    /// <inheritdoc />
    public override async ValueTask<object?> FindAsync(Type entityType, object?[]? keyValues, CancellationToken cancellationToken)
    {
        var entity = await base.FindAsync(entityType, keyValues, cancellationToken);
        return ValidateTenantAccess(entity, entityType);
    }

    /// <inheritdoc />
    public override TEntity? Find<TEntity>(params object?[]? keyValues) where TEntity : class
    {
        var entity = base.Find<TEntity>(keyValues);
        return ValidateTenantAccess(entity);
    }

    /// <inheritdoc />
    public override async ValueTask<TEntity?> FindAsync<TEntity>(params object?[]? keyValues) where TEntity : class
    {
        var entity = await base.FindAsync<TEntity>(keyValues);
        return ValidateTenantAccess(entity);
    }

    /// <inheritdoc />
    public override async ValueTask<TEntity?> FindAsync<TEntity>(object?[]? keyValues, CancellationToken cancellationToken) where TEntity : class
    {
        var entity = await base.FindAsync<TEntity>(keyValues, cancellationToken);
        return ValidateTenantAccess(entity);
    }

    /// <summary>
    /// Updates audit fields on entities that implement <see cref="IAuditableEntity"/>.
    /// </summary>
    private void UpdateAuditableEntities()
    {
        var now = timeProvider.GetUtcNow();
        var userId = CurrentUserId;

        if (EnableSoftDeleteFilters)
        {
            foreach (var entry in ChangeTracker.Entries<ISoftDeletable>())
            {
                if (entry.State == EntityState.Deleted)
                {
                    // Convert hard delete to soft delete
                    entry.State = EntityState.Modified;
                    SetPropertyValue(entry, nameof(ISoftDeletable.IsDeleted), true);
                    SetPropertyValue(entry, nameof(ISoftDeletable.DeletedAt), now);
                    if (userId is not null)
                    {
                        SetPropertyValue(entry, nameof(ISoftDeletable.DeletedBy), userId);
                    }
                }
            }
        }

        // Handle audit fields
        foreach (var entry in ChangeTracker.Entries<IAuditableEntity>())
        {
            if (entry.State == EntityState.Added)
            {
                SetPropertyValue(entry, nameof(IAuditableEntity.CreatedAt), now);
                if (userId is not null)
                {
                    SetPropertyValue(entry, nameof(IAuditableEntity.CreatedBy), userId);
                }
            }
            else if (entry.State == EntityState.Modified)
            {
                SetPropertyValue(entry, nameof(IAuditableEntity.LastModifiedAt), now);
                if (userId is not null)
                {
                    SetPropertyValue(entry, nameof(IAuditableEntity.LastModifiedBy), userId);
                }
            }
        }
    }

    /// <summary>
    /// Sets a property value on an entity entry if the property has a setter.
    /// </summary>
    private static void SetPropertyValue<T>(EntityEntry<T> entry, string propertyName, object? value)
        where T : class
    {
        var property = entry.Property(propertyName);
        if (property.Metadata.PropertyInfo?.CanWrite == true)
        {
            property.CurrentValue = value;
        }
    }

    /// <summary>
    /// Validates that a tenant entity belongs to the current tenant context.
    /// Returns null if entity doesn't match current tenant, preventing cross-tenant access via Find.
    /// </summary>
    private TEntity? ValidateTenantAccess<TEntity>(TEntity? entity) where TEntity : class
    {
        if (entity is null)
        {
            return null;
        }

        if (EnableSoftDeleteFilters && entity is ISoftDeletable softDeletable && softDeletable.IsDeleted)
        {
            return null;
        }

        if (!EnableTenantFilters || entity is not ITenantEntity tenantEntity)
        {
            return entity;
        }

        var entityTenantId = tenantEntity.TenantId;
        var currentTenantId = EffectiveFilterTenantId;

        // Check if entity belongs to current tenant
        if (currentTenantId is null)
        {
            // No tenant context - only allow access to global records
            return entityTenantId is null ? entity : null;
        }

        // Tenant context exists
        if (entityTenantId == currentTenantId)
        {
            return entity; // Belongs to current tenant
        }

        if (IncludeGlobalRecordsForTenants && entityTenantId is null)
        {
            return entity; // Global record allowed
        }

        // Entity belongs to different tenant - block access
        return null;
    }

    /// <summary>
    /// Validates tenant access for non-generic Find overload.
    /// </summary>
    private object? ValidateTenantAccess(object? entity, Type entityType)
    {
        if (entity is null)
        {
            return null;
        }

        if (EnableSoftDeleteFilters && entity is ISoftDeletable softDeletable && softDeletable.IsDeleted)
        {
            return null;
        }

        if (!EnableTenantFilters || !typeof(ITenantEntity).IsAssignableFrom(entityType))
        {
            return entity;
        }

        var tenantEntity = (ITenantEntity)entity;
        var entityTenantId = tenantEntity.TenantId;
        var currentTenantId = EffectiveFilterTenantId;

        if (currentTenantId is null)
        {
            return entityTenantId is null ? entity : null;
        }

        if (entityTenantId == currentTenantId)
        {
            return entity;
        }

        if (IncludeGlobalRecordsForTenants && entityTenantId is null)
        {
            return entity;
        }

        return null;
    }

    /// <inheritdoc />
    protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
    {
        base.OnConfiguring(optionsBuilder);

        if (TenantIsolationStrategy == TenantIsolationStrategy.PerTenantModel)
        {
            var coreOptions = optionsBuilder.Options.FindExtension<CoreOptionsExtension>();
            if (coreOptions?.ApplicationServiceProvider is not null)
            {
                throw new InvalidOperationException(
                    "PerTenantModel strategy cannot be used with UseInternalServiceProvider. " +
                    "Either switch to RuntimeTenantAccess or remove the custom service provider.");
            }

            optionsBuilder.ReplaceService<IModelCacheKeyFactory, TenantModelCacheKeyFactory>();
        }
    }

    /// <inheritdoc />
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        ApplyTenantIdConversions(modelBuilder);
        ApplyConcurrencyTokens(modelBuilder, Database.IsNpgsql());

        if (EnableTenantFilters)
        {
            ApplyTenantQueryFilters(modelBuilder);
        }

        if (EnableSoftDeleteFilters)
        {
            ApplySoftDeleteQueryFilters(modelBuilder);
        }
    }

    private static void ApplyTenantIdConversions(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties().Where(p => p.ClrType == typeof(TenantId) || p.ClrType == typeof(TenantId?)))
            {
                if (property.ClrType == typeof(TenantId))
                {
                    ConfigureTenantIdProperty(property);
                }
                else
                {
                    ConfigureNullableTenantIdProperty(property);
                }
            }
        }
    }

    private void ApplyTenantQueryFilters(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (!typeof(ITenantEntity).IsAssignableFrom(entityType.ClrType))
            {
                continue;
            }

            var parameter = Expression.Parameter(entityType.ClrType, "entity");
            var tenantProperty = Expression.Property(parameter, nameof(ITenantEntity.TenantId));

            // Build tenant filter according to the selected isolation strategy.
            var currentTenant = GetCurrentTenantExpression();
            var tenantPredicate = BuildTenantPredicate(
                tenantProperty,
                currentTenant,
                IncludeGlobalRecordsForTenants);

            // Combine with soft delete filter if entity supports it
            Expression finalPredicate = tenantPredicate;
            if (EnableSoftDeleteFilters && typeof(ISoftDeletable).IsAssignableFrom(entityType.ClrType))
            {
                var isDeletedProperty = Expression.Property(parameter, nameof(ISoftDeletable.IsDeleted));
                var notDeleted = Expression.Equal(isDeletedProperty, Expression.Constant(false));
                finalPredicate = Expression.AndAlso(tenantPredicate, notDeleted);
            }

            var filter = Expression.Lambda(finalPredicate, parameter);
            entityType.SetQueryFilter(filter);
        }
    }

    private void ApplySoftDeleteQueryFilters(ModelBuilder modelBuilder)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (!typeof(ISoftDeletable).IsAssignableFrom(entityType.ClrType))
            {
                continue;
            }

            // Skip if tenant filter already applied (it includes soft delete)
            if (typeof(ITenantEntity).IsAssignableFrom(entityType.ClrType))
            {
                continue;
            }

            var parameter = Expression.Parameter(entityType.ClrType, "entity");
            var isDeletedProperty = Expression.Property(parameter, nameof(ISoftDeletable.IsDeleted));
            var notDeleted = Expression.Equal(isDeletedProperty, Expression.Constant(false));

            var filter = Expression.Lambda(notDeleted, parameter);
            entityType.SetQueryFilter(filter);
        }
    }

    private static void ConfigureTenantIdProperty(IMutableProperty property)
    {
        property.SetValueConverter(TenantIdConverter);
        property.SetValueComparer(TenantIdComparer);
    }

    private static void ConfigureNullableTenantIdProperty(IMutableProperty property)
    {
        property.SetValueConverter(NullableTenantIdConverter);
        property.SetValueComparer(NullableTenantIdComparer);
    }

    /// <summary>
    /// Configures optimistic concurrency for entities implementing <see cref="IConcurrencyAware"/>.
    /// </summary>
    /// <remarks>
    /// On SQL Server (and other non-Npgsql providers), <c>byte[] RowVersion</c> with
    /// <c>IsRowVersion()</c> maps to a server-maintained <c>rowversion</c> column — correct.
    ///
    /// On Npgsql (PostgreSQL), <c>byte[] IsRowVersion()</c> maps to <c>bytea</c>, which is
    /// <em>not</em> server-populated. PostgreSQL never updates it, so <c>DbUpdateConcurrencyException</c>
    /// would never fire. The Npgsql EF Core convention maps a <c>uint</c> property marked
    /// <c>IsRowVersion()</c> (concurrency token + OnAddOrUpdate) to the PostgreSQL system column
    /// <c>xmin</c>, which is server-maintained and changes on every row write.
    ///
    /// Fix: on Npgsql, add a shadow <c>uint</c> property configured as <c>IsRowVersion()</c> so
    /// Npgsql's convention routes it to <c>xmin</c>. The <c>byte[] RowVersion</c> column stays
    /// mapped as a plain column; dropping it under Postgres is a deferred migration-level cleanup.
    /// </remarks>
    private static void ApplyConcurrencyTokens(ModelBuilder modelBuilder, bool isNpgsql)
    {
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            if (!typeof(IConcurrencyAware).IsAssignableFrom(entityType.ClrType))
            {
                continue;
            }

            var builder = modelBuilder.Entity(entityType.ClrType);

            if (isNpgsql)
            {
                // PostgreSQL: byte[] IsRowVersion() maps to non-server-populated bytea — concurrency
                // check never fires. Instead, add a uint shadow property with IsRowVersion() so that
                // Npgsql's NpgsqlPostgresModelFinalizingConvention maps it to the server-maintained
                // system column xmin (which increments on every row write).
                builder.Property<uint>("xmin").IsRowVersion();

                // Keep the byte[] RowVersion column mapped as a plain column for schema compatibility.
                // Dropping it under Postgres is a deferred migration-level cleanup.
                var rowVersionProp = entityType.FindProperty(nameof(IConcurrencyAware.RowVersion));
                if (rowVersionProp is not null)
                {
                    builder.Property(nameof(IConcurrencyAware.RowVersion));
                }
            }
            else
            {
                var rowVersionProp = entityType.FindProperty(nameof(IConcurrencyAware.RowVersion));
                if (rowVersionProp is not null)
                {
                    builder.Property(nameof(IConcurrencyAware.RowVersion)).IsRowVersion();
                }
            }
        }
    }

    /// <summary>
    /// Compares two tenant identifiers for equality, tolerating null values.
    /// </summary>
    private static bool AreTenantIdsEqual(TenantId left, TenantId right) =>
        StringComparer.Ordinal.Equals(left.Value, right.Value);

    /// <summary>
    /// Produces a hash code for a tenant identifier, safely handling unset values.
    /// </summary>
    private static int GetTenantIdHashCode(TenantId tenantId) =>
        string.IsNullOrEmpty(tenantId.Value) ? 0 : StringComparer.Ordinal.GetHashCode(tenantId.Value);

    /// <inheritdoc />
    public override void Dispose()
    {
        if (TenantIsolationStrategy == TenantIsolationStrategy.RuntimeTenantAccess && tenantContextApplied)
        {
            TenantContextAccessor.CurrentTenantId = previousTenantId;
        }

        base.Dispose();
    }

    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        if (TenantIsolationStrategy == TenantIsolationStrategy.RuntimeTenantAccess && tenantContextApplied)
        {
            TenantContextAccessor.CurrentTenantId = previousTenantId;
        }

        await base.DisposeAsync();
    }

    /// <summary>
    /// Resolves the current tenant expression for query filters based on the configured strategy.
    /// </summary>
    /// <returns>An expression that yields the current tenant id.</returns>
    private Expression GetCurrentTenantExpression() =>
        TenantIsolationStrategy == TenantIsolationStrategy.PerTenantModel
            ? Expression.Constant(CurrentTenantId, typeof(TenantId?))
            : Expression.Property(
                null,
                typeof(TenantContextAccessor),
                nameof(TenantContextAccessor.CurrentTenantId));

    /// <summary>
    /// Builds the tenant predicate that enforces tenant isolation and optional global record inclusion.
    /// </summary>
    private static Expression BuildTenantPredicate(
        Expression tenantProperty,
        Expression currentTenant,
        bool includeGlobalRecordsForTenants)
    {
        var nullTenant = Expression.Constant(null, typeof(TenantId?));
        var tenantIsNull = Expression.Equal(currentTenant, nullTenant);
        var tenantMatches = Expression.Equal(tenantProperty, currentTenant);
        var entityIsGlobal = Expression.Equal(tenantProperty, nullTenant);

        return includeGlobalRecordsForTenants
            ? Expression.OrElse(
                Expression.AndAlso(tenantIsNull, entityIsGlobal),
                Expression.AndAlso(
                    Expression.Not(tenantIsNull),
                    Expression.OrElse(tenantMatches, entityIsGlobal)))
            : Expression.OrElse(
                Expression.AndAlso(tenantIsNull, entityIsGlobal),
                Expression.AndAlso(Expression.Not(tenantIsNull), tenantMatches));
    }
}
