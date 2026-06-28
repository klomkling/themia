namespace Themia.Modules.Export.Requests;

/// <summary>A read-only projection of an export run for callers (history, polling, download metadata).</summary>
/// <param name="Id">The run identifier.</param>
/// <param name="DefinitionKey">The export definition key.</param>
/// <param name="Format">The output format.</param>
/// <param name="Status">The lifecycle status.</param>
/// <param name="StorageKey">The Storage object key once produced, or null.</param>
/// <param name="SizeBytes">The produced byte length, or null.</param>
/// <param name="ExpiresAt">When the stored bytes expire, or null.</param>
/// <param name="Error">The failure message, or null.</param>
/// <param name="CreatedAt">When the run was created.</param>
/// <param name="CompletedAt">When the run reached a terminal state, or null.</param>
public sealed record ExportRunView(
    Guid Id,
    string DefinitionKey,
    ExportFormat Format,
    ExportRunStatus Status,
    string? StorageKey,
    long? SizeBytes,
    DateTimeOffset? ExpiresAt,
    string? Error,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt);
