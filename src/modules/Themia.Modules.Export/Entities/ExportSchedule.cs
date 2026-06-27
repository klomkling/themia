using Themia.Framework.Core.Abstractions.Entities;
using Themia.Framework.Core.Abstractions.Tenancy;

namespace Themia.Modules.Export.Entities;

/// <summary>A recurring export schedule (cron) that produces runs.</summary>
public sealed class ExportSchedule : SoftDeletableEntity<Guid>, ITenantEntity
{
    /// <inheritdoc />
    public TenantId? TenantId { get; set; }

    /// <summary>The user notified for each produced run.</summary>
    public string? UserId { get; set; }

    /// <summary>The export definition key.</summary>
    public string DefinitionKey { get; set; } = string.Empty;

    /// <summary>The fixed filter/scope parameters (relative values resolved at fire time).</summary>
    public string? ParametersJson { get; set; }

    /// <summary>The requested format.</summary>
    public ExportFormat Format { get; set; }

    /// <summary>The Quartz cron expression.</summary>
    public string Cron { get; set; } = string.Empty;

    /// <summary>Whether the schedule is active.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Whether produced runs include soft-deleted rows.</summary>
    public bool IncludeSoftDeleted { get; set; }

    /// <summary>Sets the identity (factory use).</summary>
    public void SetId(Guid id) => Id = id;
}
