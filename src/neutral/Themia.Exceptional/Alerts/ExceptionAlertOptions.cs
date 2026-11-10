namespace Themia.Exceptional.Alerts;

/// <summary>Configuration for the exception alert job and its Telegram channel. Off unless <see cref="Enabled"/>.</summary>
public sealed class ExceptionAlertOptions
{
    /// <summary>Turns the alert job on. Default <see langword="false"/>.</summary>
    public bool Enabled { get; set; }

    /// <summary>Telegram bot token (secret). Required when enabled. Never logged.</summary>
    public string BotToken { get; set; } = string.Empty;

    /// <summary>Telegram chat id (or <c>@channel</c>). Required when enabled.</summary>
    public string ChatId { get; set; } = string.Empty;

    /// <summary>Optional forum topic id (<c>message_thread_id</c>).</summary>
    public int? TopicId { get; set; }

    /// <summary>Environment label shown in the message. Falls back to the host environment name.</summary>
    public string? EnvironmentName { get; set; }

    /// <summary>Admin page link, with <c>{guid}</c> replaced by the exception's Guid. Required when enabled.</summary>
    public string AdminUrlTemplate { get; set; } = string.Empty;

    /// <summary>How often the job looks for alertable rows. Default 1 minute.</summary>
    public TimeSpan Interval { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>A hash quiet for longer than this and then seen again alerts as <see cref="ExceptionAlertTrigger.Reappeared"/>. Default 24 hours.</summary>
    public TimeSpan QuietPeriod { get; set; } = TimeSpan.FromHours(24);

    /// <summary>Only rows last seen within this window are considered. Default 1 hour.</summary>
    public TimeSpan Lookback { get; set; } = TimeSpan.FromHours(1);

    /// <summary>A row whose duplicate count reaches this alerts as <see cref="ExceptionAlertTrigger.Threshold"/>. Default 50.</summary>
    public int CountThreshold { get; set; } = 50;

    /// <summary>Maximum messages sent per hour (best-effort across instances). Default 12.</summary>
    public int MaxMessagesPerHour { get; set; } = 12;

    /// <summary>Maximum groups listed in one digest message. Default 10.</summary>
    public int MaxGroupsPerMessage { get; set; } = 10;

    /// <summary>How long a claim is held before another instance may reclaim it. Default 5 minutes.</summary>
    public TimeSpan Lease { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>Claims retried at most this many times before being marked failed. Default 5.</summary>
    public int MaxAttempts { get; set; } = 5;

    /// <summary>Timeout of one Telegram request. Default 10 seconds.</summary>
    public TimeSpan SendTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>Transient-error retries inside one send. Default 3.</summary>
    public int MaxSendRetries { get; set; } = 3;

    private const int MaxSendRetriesCeiling = 10;

    /// <summary>Longest Telegram <c>retry_after</c> that is waited for inside one send. Default 60 seconds.</summary>
    public TimeSpan MaxRetryAfter { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Base delay of the exponential retry backoff. Default 1 second.</summary>
    public TimeSpan RetryBaseDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>Validates the options. Throws <see cref="InvalidOperationException"/> on invalid configuration.</summary>
    public void Validate()
    {
        if (!Enabled)
            return;
        Require(!string.IsNullOrWhiteSpace(BotToken), nameof(BotToken) + " must be set when alerts are enabled.");
        Require(!string.IsNullOrWhiteSpace(ChatId), nameof(ChatId) + " must be set when alerts are enabled.");
        Require(AdminUrlTemplate.Contains("{guid}", StringComparison.Ordinal),
            nameof(AdminUrlTemplate) + " must be set and contain {guid}.");
        Require(Interval > TimeSpan.Zero, nameof(Interval) + " must be positive.");
        Require(QuietPeriod > TimeSpan.Zero, nameof(QuietPeriod) + " must be positive.");
        Require(Lookback > TimeSpan.Zero, nameof(Lookback) + " must be positive.");
        Require(CountThreshold > 0, nameof(CountThreshold) + " must be positive.");
        Require(MaxMessagesPerHour > 0, nameof(MaxMessagesPerHour) + " must be positive.");
        Require(MaxGroupsPerMessage > 0, nameof(MaxGroupsPerMessage) + " must be positive.");
        Require(MaxAttempts > 0, nameof(MaxAttempts) + " must be positive.");
        Require(MaxSendRetries is >= 0 and <= MaxSendRetriesCeiling, nameof(MaxSendRetries) + $" must be between 0 and {MaxSendRetriesCeiling}.");
        Require(SendTimeout > TimeSpan.Zero, nameof(SendTimeout) + " must be positive.");

        // Each retry may wait out a full 429 retry_after or a jittered (up to x1.5) exponential backoff.
        var backoffTicks = (long)(RetryBaseDelay.Ticks * ((1L << (MaxSendRetries + 1)) - 1) * 1.5);
        var worstSend = TimeSpan.FromTicks(SendTimeout.Ticks * (MaxSendRetries + 1))
            + TimeSpan.FromTicks(MaxRetryAfter.Ticks * MaxSendRetries) + TimeSpan.FromTicks(backoffTicks);
        Require(Lease > worstSend,
            $"{nameof(Lease)} ({Lease}) must exceed the longest possible send ({worstSend}); otherwise another instance can reclaim a batch that is still being sent.");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException("ExceptionAlertOptions." + message);
    }
}
