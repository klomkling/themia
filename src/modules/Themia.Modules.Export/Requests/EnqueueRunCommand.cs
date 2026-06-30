using Themia.Framework.Core.Abstractions.Tenancy;

namespace Themia.Modules.Export.Requests;

/// <summary>The cohesive parameter bundle for <see cref="IExportRunEnqueuer.EnqueueRunAsync"/>: everything
/// needed to persist a Pending run and schedule its one-shot job, in one object so positional arguments
/// cannot be transposed.</summary>
/// <param name="DefinitionKey">The registered export definition key.</param>
/// <param name="ParametersJson">The filter/scope parameters (System.Text.Json), or null.</param>
/// <param name="Format">The requested output format.</param>
/// <param name="FileName">The suggested download file name, or null.</param>
/// <param name="IncludeSoftDeleted">Whether to include soft-deleted rows (definition must allow it).</param>
/// <param name="UserId">The requesting user (notification target), or null.</param>
/// <param name="TenantId">The owning tenant the run is stamped with.</param>
internal sealed record EnqueueRunCommand(
    string DefinitionKey,
    string? ParametersJson,
    ExportFormat Format,
    string? FileName,
    bool IncludeSoftDeleted,
    string? UserId,
    TenantId? TenantId);
