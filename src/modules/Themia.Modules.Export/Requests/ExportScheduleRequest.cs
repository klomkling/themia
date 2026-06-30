namespace Themia.Modules.Export.Requests;

/// <summary>A recurring export request: persists a schedule and registers a cron trigger that produces a
/// run on each fire.</summary>
/// <param name="DefinitionKey">The registered export definition key.</param>
/// <param name="Cron">The Quartz cron expression that drives the schedule.</param>
/// <param name="Format">The requested output format.</param>
/// <param name="ParametersJson">The fixed filter/scope parameters; relative markers resolve at fire time.</param>
/// <param name="IncludeSoftDeleted">Whether produced runs include soft-deleted rows (definition must allow it).</param>
/// <param name="UserId">The user notified for each produced run, or null.</param>
public sealed record ExportScheduleRequest(
    string DefinitionKey,
    string Cron,
    ExportFormat Format,
    string? ParametersJson = null,
    bool IncludeSoftDeleted = false,
    string? UserId = null);
