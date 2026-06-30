namespace Themia.Modules.Export.Requests;

/// <summary>An on-demand export request: produces a single run that fires immediately.</summary>
/// <param name="DefinitionKey">The registered export definition key.</param>
/// <param name="ParametersJson">The filter/scope parameters (System.Text.Json), or null.</param>
/// <param name="Format">The requested output format.</param>
/// <param name="FileName">The suggested download file name, or null to let the definition choose.</param>
/// <param name="IncludeSoftDeleted">Whether to include soft-deleted rows (definition must allow it).</param>
/// <param name="UserId">The requesting user (notification target), or null.</param>
public sealed record ExportSubmission(
    string DefinitionKey,
    string? ParametersJson,
    ExportFormat Format,
    string? FileName = null,
    bool IncludeSoftDeleted = false,
    string? UserId = null);
