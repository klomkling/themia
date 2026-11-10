using System.Net;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Themia.Exceptional.Alerts;
using Xunit;

namespace Themia.Exceptional.Tests;

public class TelegramAlertChannelTests
{
    private const string Token = "123456789:AAH-secret-bot-token-value";

    private sealed class ScriptedHandler(Func<HttpRequestMessage, int, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            return respond(request, Requests.Count);
        }
    }

    private sealed class CapturingProvider : ILoggerProvider
    {
        public List<string> Lines { get; } = [];
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, Lines);
        public void Dispose() { }

        private sealed class CapturingLogger(string category, List<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel logLevel) => true;
            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (lines)
                    lines.Add($"{category}|{formatter(state, exception)}|{exception}");
            }
        }
    }

    private static ExceptionAlertOptions Options() => new()
    {
        Enabled = true,
        BotToken = Token,
        ChatId = "-1001",
        AdminUrlTemplate = "https://admin.example.test/exceptions/{guid}",
        RetryBaseDelay = TimeSpan.Zero,
    };

    private static ServiceProvider Build(ScriptedHandler handler, CapturingProvider logs, ExceptionAlertOptions? options = null)
    {
        var o = options ?? Options();
        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace).AddProvider(logs));
        services.AddThemiaExceptionalAlerts(x =>
        {
            x.Enabled = o.Enabled; x.BotToken = o.BotToken; x.ChatId = o.ChatId; x.TopicId = o.TopicId;
            x.AdminUrlTemplate = o.AdminUrlTemplate; x.RetryBaseDelay = o.RetryBaseDelay;
            x.MaxSendRetries = o.MaxSendRetries; x.MaxRetryAfter = o.MaxRetryAfter;
        });
        services.AddHttpClient(TelegramAlertChannel.HttpClientName).ConfigurePrimaryHttpMessageHandler(() => handler);
        return services.BuildServiceProvider();
    }

    private static HttpResponseMessage Json(HttpStatusCode code, string json)
        => new(code) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    [Fact]
    public async Task Send_PostsPlainTextToSendMessageWithChatAndTopic()
    {
        var handler = new ScriptedHandler((_, _) => Json(HttpStatusCode.OK, """{"ok":true}"""));
        var options = Options();
        options.TopicId = 7;
        using var sp = Build(handler, new CapturingProvider(), options);

        var outcome = await sp.GetRequiredService<IAlertChannel>().SendAsync(new AlertDigest("hello", 1), default);

        Assert.Equal(AlertSendOutcome.Sent, outcome);
        Assert.Equal("https://api.telegram.org/bot" + Token + "/sendMessage", handler.Requests[0].RequestUri!.ToString());
        Assert.Contains("\"chat_id\":\"-1001\"", handler.Bodies[0]);
        Assert.Contains("\"message_thread_id\":7", handler.Bodies[0]);
        Assert.Contains("\"text\":\"hello\"", handler.Bodies[0]);
        Assert.DoesNotContain("parse_mode", handler.Bodies[0]);
    }

    [Fact]
    public async Task Send_ServerErrorThenOk_RetriesAndSucceeds()
    {
        var handler = new ScriptedHandler((_, n) => n < 3
            ? Json(HttpStatusCode.BadGateway, "{}") : Json(HttpStatusCode.OK, "{}"));
        using var sp = Build(handler, new CapturingProvider());

        var outcome = await sp.GetRequiredService<IAlertChannel>().SendAsync(new AlertDigest("x", 1), default);

        Assert.Equal(AlertSendOutcome.Sent, outcome);
        Assert.Equal(3, handler.Requests.Count);
    }

    [Fact]
    public async Task Send_ServerErrorForever_GivesUpAsTransientAfterCap()
    {
        var handler = new ScriptedHandler((_, _) => Json(HttpStatusCode.InternalServerError, "{}"));
        using var sp = Build(handler, new CapturingProvider());

        var outcome = await sp.GetRequiredService<IAlertChannel>().SendAsync(new AlertDigest("x", 1), default);

        Assert.Equal(AlertSendOutcome.Transient, outcome);
        Assert.Equal(4, handler.Requests.Count); // 1 try + MaxSendRetries(3)
    }

    [Fact]
    public async Task Send_BadRequest_IsPermanentAndNotRetried()
    {
        var handler = new ScriptedHandler((_, _) =>
            Json(HttpStatusCode.BadRequest, """{"ok":false,"description":"Bad Request: chat not found"}"""));
        using var sp = Build(handler, new CapturingProvider());

        var outcome = await sp.GetRequiredService<IAlertChannel>().SendAsync(new AlertDigest("x", 1), default);

        Assert.Equal(AlertSendOutcome.Permanent, outcome);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task Send_TooManyRequests_WaitsForRetryAfterThenSucceeds()
    {
        var waits = new List<TimeSpan>();
        var handler = new ScriptedHandler((_, n) => n == 1
            ? Json((HttpStatusCode)429, """{"ok":false,"parameters":{"retry_after":7}}""")
            : Json(HttpStatusCode.OK, "{}"));
        using var sp = Build(handler, new CapturingProvider());
        var factory = sp.GetRequiredService<IHttpClientFactory>();
        var channel = new TelegramAlertChannel(factory, Options(),
            sp.GetRequiredService<ILogger<TelegramAlertChannel>>(), (d, _) => { waits.Add(d); return Task.CompletedTask; });

        var outcome = await channel.SendAsync(new AlertDigest("x", 1), default);

        Assert.Equal(AlertSendOutcome.Sent, outcome);
        Assert.Equal([TimeSpan.FromSeconds(7)], waits);
    }

    [Fact]
    public async Task Send_RetryAfterBeyondCap_ReturnsTransientWithoutWaiting()
    {
        var waits = new List<TimeSpan>();
        var handler = new ScriptedHandler((_, _) =>
            Json((HttpStatusCode)429, """{"ok":false,"parameters":{"retry_after":3600}}"""));
        using var sp = Build(handler, new CapturingProvider());
        var channel = new TelegramAlertChannel(sp.GetRequiredService<IHttpClientFactory>(), Options(),
            sp.GetRequiredService<ILogger<TelegramAlertChannel>>(), (d, _) => { waits.Add(d); return Task.CompletedTask; });

        var outcome = await channel.SendAsync(new AlertDigest("x", 1), default);

        Assert.Equal(AlertSendOutcome.Transient, outcome);
        Assert.Single(handler.Requests);
        Assert.Empty(waits);
    }

    [Fact]
    public async Task Send_NeverLogsTheBotToken_EvenWhenTheRequestFails()
    {
        var logs = new CapturingProvider();
        var handler = new ScriptedHandler((req, n) => n switch
        {
            1 => throw new HttpRequestException($"connection refused to {req.RequestUri}"),
            2 => Json(HttpStatusCode.InternalServerError, "{}"),
            _ => Json(HttpStatusCode.OK, "{}"),
        });
        using var sp = Build(handler, logs);

        await sp.GetRequiredService<IAlertChannel>().SendAsync(new AlertDigest("x", 1), default);

        Assert.NotEmpty(logs.Lines);
        Assert.DoesNotContain(logs.Lines, l => l.Contains(Token) || l.Contains("AAH-secret"));
        // The factory's own handler loggers must be gone, not merely quiet for this run.
        Assert.DoesNotContain(logs.Lines, l => l.StartsWith("System.Net.Http.HttpClient."));
    }
}
