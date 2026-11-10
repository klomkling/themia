namespace Themia.Exceptional.Alerts;

/// <summary>Why a stored exception row qualifies for an alert. Lower value = higher priority under the hourly cap.</summary>
public enum ExceptionAlertTrigger
{
    /// <summary>The first row ever stored for this application + error hash.</summary>
    New = 1,

    /// <summary>The error came back after being quiet for longer than the configured quiet period.</summary>
    Reappeared = 2,

    /// <summary>The row's duplicate count reached the configured threshold within one rollup window.</summary>
    Threshold = 3,
}
