using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Themia.Exceptional.Alerts;

/// <summary>Sends digests through the Telegram Bot API <c>sendMessage</c> method.</summary>
/// <remarks>
/// The bot token is part of the request URL, so this class never logs the URL, the request, or the message of
/// an <see cref="HttpRequestException"/>; only status codes and Telegram's own error description are logged.
/// </remarks>
public sealed partial class TelegramAlertChannel : IAlertChannel
{
    /// <summary>Name of the <see cref="HttpClient"/> this channel resolves from <see cref="IHttpClientFactory"/>.</summary>
    public const string HttpClientName = "Themia.Exceptional.Telegram";

    private const string TelegramApi = "https://api.telegram.org/";
    private const int TooManyRequests = 429;
    private const int ServerErrorFloor = 500;
    private const double JitterSpread = 0.5;

    private readonly IHttpClientFactory httpClientFactory;
    private readonly ExceptionAlertOptions options;
    private readonly ILogger<TelegramAlertChannel> logger;
    private readonly Func<TimeSpan, CancellationToken, Task> delay;

    /// <summary>Creates the channel.</summary>
    public TelegramAlertChannel(
        IHttpClientFactory httpClientFactory, ExceptionAlertOptions options, ILogger<TelegramAlertChannel> logger)
        : this(httpClientFactory, options, logger, Task.Delay)
    {
    }

    internal TelegramAlertChannel(
        IHttpClientFactory httpClientFactory,
        ExceptionAlertOptions options,
        ILogger<TelegramAlertChannel> logger,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        this.httpClientFactory = httpClientFactory;
        this.options = options;
        this.logger = logger;
        this.delay = delay;
    }

    /// <inheritdoc />
    public async Task<AlertSendOutcome> SendAsync(AlertDigest digest, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(digest);
        for (var attempt = 0; ; attempt++)
        {
            var result = await TrySendOnceAsync(digest, cancellationToken).ConfigureAwait(false);
            if (!result.RetryInThisSend || attempt >= options.MaxSendRetries)
                return result.Outcome;
            await delay(result.Wait ?? Backoff(attempt), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<Attempt> TrySendOnceAsync(
        AlertDigest digest, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(options.SendTimeout);
        try
        {
            using var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.PostAsJsonAsync(
                new Uri($"{TelegramApi}bot{options.BotToken}/sendMessage"), BuildBody(digest), BodyContext.Default.TelegramBody, timeout.Token)
                .ConfigureAwait(false);
            return await ClassifyAsync(response, timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Telegram alert send timed out after {Timeout}.", options.SendTimeout);
            return Attempt.RetryWithBackoff;
        }
        catch (HttpRequestException ex)
        {
            // Deliberately not logging ex: its message/inner chain can carry the request URI (the bot token).
            logger.LogWarning("Telegram alert send failed with {ExceptionType} (status {StatusCode}).",
                ex.GetType().Name, ex.StatusCode);
            return Attempt.RetryWithBackoff;
        }
    }

    private async Task<Attempt> ClassifyAsync(HttpResponseMessage response, CancellationToken ct)
    {
        var status = (int)response.StatusCode;
        if (response.IsSuccessStatusCode)
            return Attempt.Delivered;

        var error = await ReadErrorAsync(response, ct).ConfigureAwait(false);
        if (status == TooManyRequests)
        {
            var retryAfter = TimeSpan.FromSeconds(error?.Parameters?.RetryAfter ?? 1);
            logger.LogWarning("Telegram rate-limited the alert send; retry_after {RetryAfter}.", retryAfter);
            // A wait beyond the cap is not slept through here; the batch stays claimed and is retried after its lease.
            return retryAfter <= options.MaxRetryAfter ? Attempt.RetryAfter(retryAfter) : Attempt.GiveUpForNow;
        }

        if (status >= ServerErrorFloor)
        {
            logger.LogWarning("Telegram alert send got server error {StatusCode}.", status);
            return Attempt.RetryWithBackoff;
        }

        logger.LogError("Telegram rejected the alert send: {StatusCode} {Description}.", status, error?.Description);
        return Attempt.Rejected;
    }

    private static async Task<TelegramError?> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            return await response.Content.ReadFromJsonAsync(BodyContext.Default.TelegramError, ct).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private TimeSpan Backoff(int attempt)
    {
        var jitter = 1 + ((Random.Shared.NextDouble() - 0.5) * JitterSpread * 2);
        return TimeSpan.FromTicks((long)(options.RetryBaseDelay.Ticks * Math.Pow(2, attempt) * jitter));
    }

    private TelegramBody BuildBody(AlertDigest digest) => new()
    {
        ChatId = options.ChatId,
        Text = digest.Text,
        MessageThreadId = options.TopicId,
        DisableWebPagePreview = true,
    };

    private readonly record struct Attempt(AlertSendOutcome Outcome, TimeSpan? Wait, bool RetryInThisSend)
    {
        public static Attempt Delivered => new(AlertSendOutcome.Sent, null, false);
        public static Attempt Rejected => new(AlertSendOutcome.Permanent, null, false);
        public static Attempt GiveUpForNow => new(AlertSendOutcome.Transient, null, false);
        public static Attempt RetryWithBackoff => new(AlertSendOutcome.Transient, null, true);
        public static Attempt RetryAfter(TimeSpan wait) => new(AlertSendOutcome.Transient, wait, true);
    }

    internal sealed class TelegramBody
    {
        [JsonPropertyName("chat_id")] public string ChatId { get; init; } = string.Empty;
        [JsonPropertyName("text")] public string Text { get; init; } = string.Empty;
        [JsonPropertyName("message_thread_id"), JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public int? MessageThreadId { get; init; }
        [JsonPropertyName("disable_web_page_preview")] public bool DisableWebPagePreview { get; init; }
    }

    internal sealed class TelegramError
    {
        [JsonPropertyName("description")] public string? Description { get; init; }
        [JsonPropertyName("parameters")] public TelegramErrorParameters? Parameters { get; init; }
    }

    internal sealed class TelegramErrorParameters
    {
        [JsonPropertyName("retry_after")] public int? RetryAfter { get; init; }
    }

    [JsonSerializable(typeof(TelegramBody))]
    [JsonSerializable(typeof(TelegramError))]
    internal sealed partial class BodyContext : JsonSerializerContext;
}
