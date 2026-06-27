using Themia.Framework.Core.Abstractions.Entities;
using Themia.Framework.Core.Abstractions.Tenancy;

namespace Themia.Modules.Export.Entities;

/// <summary>Persisted state of a single export run (history + retention).</summary>
public sealed class ExportRun : SoftDeletableEntity<Guid>, ITenantEntity
{
    /// <inheritdoc />
    public TenantId? TenantId { get; set; }

    /// <summary>The requesting user (notification target); null for system schedules.</summary>
    public string? UserId { get; set; }

    /// <summary>The export definition key.</summary>
    public string DefinitionKey { get; set; } = string.Empty;

    /// <summary>The filter/scope parameters (System.Text.Json).</summary>
    public string? ParametersJson { get; set; }

    /// <summary>The requested format.</summary>
    public ExportFormat Format { get; set; }

    /// <summary>The lifecycle status.</summary>
    public ExportRunStatus Status { get; set; }

    /// <summary>Whether soft-deleted rows were requested.</summary>
    public bool IncludeSoftDeleted { get; set; }

    /// <summary>The Storage object key once produced.</summary>
    public string? StorageKey { get; set; }

    /// <summary>The suggested download file name.</summary>
    public string? FileName { get; set; }

    /// <summary>The produced byte length.</summary>
    public long? SizeBytes { get; set; }

    /// <summary>When the stored bytes expire (retention).</summary>
    public DateTimeOffset? ExpiresAt { get; set; }

    /// <summary>The error message on failure.</summary>
    public string? Error { get; set; }

    /// <summary>When the run reached a terminal state.</summary>
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>Sets the identity (factory use).</summary>
    public void SetId(Guid id) => Id = id;
}
