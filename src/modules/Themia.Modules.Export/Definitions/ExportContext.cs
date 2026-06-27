using Themia.Framework.Core.Abstractions.Tenancy;

namespace Themia.Modules.Export.Definitions;

/// <summary>The ambient inputs a definition runs against. Built by the job from the persisted run/schedule.</summary>
public sealed class ExportContext
{
    /// <summary>The tenant the export runs for.</summary>
    public required TenantId? TenantId { get; init; }
    /// <summary>The user who requested it (notification target); may be null for system schedules.</summary>
    public string? UserId { get; init; }
    /// <summary>The raw filter/scope parameters (System.Text.Json); deserialized by the typed base.</summary>
    public string? ParametersJson { get; init; }
    /// <summary>The requested output format.</summary>
    public required ExportFormat Format { get; init; }
    /// <summary>An optional download file name (without the job's timestamp default).</summary>
    public string? FileName { get; init; }
    /// <summary>Whether soft-deleted business rows should be included (gated by the definition).</summary>
    public bool IncludeSoftDeleted { get; init; }
}
