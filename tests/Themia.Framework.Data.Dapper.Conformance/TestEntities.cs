using Themia.Framework.Core.Abstractions.Entities;
using Themia.Framework.Core.Abstractions.Tenancy;
using Themia.Framework.Data.Abstractions.Filtering;
using Themia.Framework.Data.Abstractions.Repositories;
using Themia.Framework.Data.Abstractions.Specifications;
using Themia.Framework.Data.Abstractions.UnitOfWork;

namespace Themia.Framework.Data.Dapper.Conformance;

/// <summary>
/// Shared tenant-aware, soft-deletable test entity. Both the Dapper and EF Core providers map this to the
/// same physical <c>widgets</c> table with snake_case columns, proving they honour the shared abstraction.
/// </summary>
public class Widget : SoftDeletableEntity<Guid>, ITenantEntity
{
    /// <summary>The owning tenant, stamped by the data layer on insert.</summary>
    public TenantId? TenantId { get; set; }

    /// <summary>The widget name.</summary>
    public string Name { get; set; } = "";

    /// <summary>The widget quantity.</summary>
    public int Quantity { get; set; }

    /// <summary>Assigns the client-generated key (<see cref="Entity{TId}.Id"/> has a protected setter).</summary>
    public void SetId(Guid id) => Id = id;
}

/// <summary>Matches widgets by exact name.</summary>
public sealed class WidgetByNameSpec : Specification<Widget>
{
    /// <summary>Creates a spec matching widgets whose name equals <paramref name="name"/>.</summary>
    public WidgetByNameSpec(string name) => Where(w => w.Name == name);
}

/// <summary>Matches widgets by exact name with the tenant filter disabled (spec-level cross-tenant access).</summary>
public sealed class WidgetByNameNoTenantSpec : Specification<Widget>
{
    /// <summary>Creates a spec matching widgets named <paramref name="name"/> across all tenants.</summary>
    public WidgetByNameNoTenantSpec(string name)
    {
        Where(w => w.Name == name);
        WithoutTenantFilter();
    }
}

/// <summary>
/// The root DI container, a resolved scope from it, and the three shared data contracts under test.
/// Disposing the record disposes the scope and then the root container that owns it.
/// </summary>
public sealed record ConformanceScope(
    IAsyncDisposable Root,
    IAsyncDisposable Scope,
    IRepository<Widget, Guid> Repo,
    IUnitOfWork Uow,
    IDataFilterScope Filter) : IAsyncDisposable
{
    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        await Scope.DisposeAsync();
        await Root.DisposeAsync();
    }
}
