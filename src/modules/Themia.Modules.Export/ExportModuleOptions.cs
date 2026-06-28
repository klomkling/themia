namespace Themia.Modules.Export;

/// <summary>Configuration options for the Export module.</summary>
public sealed class ExportModuleOptions
{
    /// <summary>How long produced export bytes are retained before expiry.</summary>
    public TimeSpan Retention { get; set; } = TimeSpan.FromDays(7);

    /// <summary>How long presigned download links remain valid.</summary>
    public TimeSpan LinkTtl { get; set; } = TimeSpan.FromHours(1);

    /// <summary>Quartz cron expression for the recurring cleanup sweep. Default daily at 03:00.</summary>
    public string CleanupCron { get; set; } = "0 0 3 * * ?";

    /// <summary>The configuration connection-string name for the export schema. Default "Default".</summary>
    public string ConnectionStringName { get; set; } = "Default";
}
