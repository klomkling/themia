namespace Themia.Modules.Export;

/// <summary>Lifecycle state of an export run.</summary>
public enum ExportRunStatus
{
    /// <summary>Queued, not yet executing.</summary>
    Pending,
    /// <summary>Executing.</summary>
    Running,
    /// <summary>Produced and stored; bytes available until <c>ExpiresAt</c>.</summary>
    Succeeded,
    /// <summary>Terminated by an error.</summary>
    Failed,
    /// <summary>Retention elapsed; bytes purged, record kept as history.</summary>
    Expired,
}
