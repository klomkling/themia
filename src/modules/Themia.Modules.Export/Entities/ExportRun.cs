using Themia.Framework.Core.Abstractions.Entities;
using Themia.Framework.Core.Abstractions.Tenancy;

namespace Themia.Modules.Export.Entities;

/// <summary>Persisted state of a single export run (history + retention). Lifecycle fields are mutated only
/// through the <c>Mark*</c> transition methods so correlated state stays consistent (e.g. a Succeeded run
/// always carries its storage key) and illegal transitions (such as expiring a non-succeeded run) fail fast.</summary>
public sealed class ExportRun : SoftDeletableEntity<Guid>, ITenantEntity
{
    /// <inheritdoc />
    public TenantId? TenantId { get; set; }

    /// <summary>The requesting user (notification target); null for system schedules.</summary>
    public string? UserId { get; set; }

    /// <summary>The export definition key.</summary>
    public required string DefinitionKey { get; set; }

    /// <summary>The filter/scope parameters (System.Text.Json).</summary>
    public string? ParametersJson { get; set; }

    /// <summary>The requested format.</summary>
    public ExportFormat Format { get; set; }

    /// <summary>Whether soft-deleted rows were requested.</summary>
    public bool IncludeSoftDeleted { get; set; }

    /// <summary>The suggested download file name (set on success to the produced name).</summary>
    public string? FileName { get; set; }

    /// <summary>The lifecycle status. A newly created run starts <see cref="ExportRunStatus.Pending"/>
    /// explicitly (not relying on the enum's zero value), so reordering the enum cannot change the entry state.</summary>
    public ExportRunStatus Status { get; private set; } = ExportRunStatus.Pending;

    /// <summary>The Storage object key once produced.</summary>
    public string? StorageKey { get; private set; }

    /// <summary>The produced byte length.</summary>
    public long? SizeBytes { get; private set; }

    /// <summary>When the stored bytes expire (retention).</summary>
    public DateTimeOffset? ExpiresAt { get; private set; }

    /// <summary>The error message on failure.</summary>
    public string? Error { get; private set; }

    /// <summary>When the run started executing (used to reconcile runs orphaned in Running by a host restart).</summary>
    public DateTimeOffset? StartedAt { get; private set; }

    /// <summary>When the run reached a terminal state.</summary>
    public DateTimeOffset? CompletedAt { get; private set; }

    /// <summary>Transitions the run to <see cref="ExportRunStatus.Running"/>, recording when it started.</summary>
    public void MarkRunning(DateTimeOffset startedAt)
    {
        Status = ExportRunStatus.Running;
        StartedAt = startedAt;
    }

    /// <summary>Transitions the run to <see cref="ExportRunStatus.Succeeded"/>, recording its stored result.</summary>
    public void MarkSucceeded(
        string storageKey, string fileName, long sizeBytes, DateTimeOffset expiresAt, DateTimeOffset completedAt)
    {
        ArgumentException.ThrowIfNullOrEmpty(storageKey);
        Status = ExportRunStatus.Succeeded;
        StorageKey = storageKey;
        FileName = fileName;
        SizeBytes = sizeBytes;
        ExpiresAt = expiresAt;
        CompletedAt = completedAt;
        Error = null;
    }

    /// <summary>Transitions the run to <see cref="ExportRunStatus.Failed"/>, recording the error and clearing
    /// any stored result so a failed run can never carry a live storage key (the retention sweep only purges
    /// Succeeded runs, so a failed run with a key would leak its blob forever).</summary>
    public void MarkFailed(string error, DateTimeOffset completedAt)
    {
        Status = ExportRunStatus.Failed;
        Error = error;
        CompletedAt = completedAt;
        StorageKey = null;
        SizeBytes = null;
        ExpiresAt = null;
    }

    /// <summary>Transitions a succeeded run to <see cref="ExportRunStatus.Expired"/> after its bytes are purged.</summary>
    /// <exception cref="InvalidOperationException">The run is not in the <see cref="ExportRunStatus.Succeeded"/> state.</exception>
    public void MarkExpired()
    {
        if (Status != ExportRunStatus.Succeeded)
        {
            throw new InvalidOperationException(
                $"Only a succeeded run can expire; run {Id} is {Status}.");
        }

        Status = ExportRunStatus.Expired;
    }

    /// <summary>Sets the identity once at creation (factory/test use); not part of the run's lifecycle.</summary>
    internal void SetId(Guid id) => Id = id;
}
