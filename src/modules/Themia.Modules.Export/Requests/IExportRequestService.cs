namespace Themia.Modules.Export.Requests;

/// <summary>The public entry point for export callers: submit on-demand runs, schedule recurring exports,
/// and read run history. All operations are scoped to the ambient tenant.</summary>
public interface IExportRequestService
{
    /// <summary>Creates a <see cref="ExportRunStatus.Pending"/> run and schedules a one-shot job that fires now.</summary>
    /// <exception cref="InvalidOperationException">The definition key is unknown, or soft-delete is requested but disallowed.</exception>
    Task<ExportRunView> SubmitAsync(ExportSubmission submission, CancellationToken cancellationToken);

    /// <summary>Persists a recurring schedule and registers a cron trigger keyed by the new schedule id.</summary>
    /// <returns>The new schedule's identifier.</returns>
    /// <exception cref="InvalidOperationException">The definition key is unknown, or soft-delete is requested but disallowed.</exception>
    Task<Guid> ScheduleAsync(ExportScheduleRequest request, CancellationToken cancellationToken);

    /// <summary>Lists the current tenant's runs (optionally filtered by user), newest first.</summary>
    Task<IReadOnlyList<ExportRunView>> ListRunsAsync(string? userId, CancellationToken cancellationToken);

    /// <summary>Gets a single run in the current tenant by id, or null if it does not exist.</summary>
    Task<ExportRunView?> GetRunAsync(Guid id, CancellationToken cancellationToken);
}
