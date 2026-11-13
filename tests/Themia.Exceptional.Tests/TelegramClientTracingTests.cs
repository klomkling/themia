using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Themia.Exceptional.Alerts;
using Xunit;

namespace Themia.Exceptional.Tests;

public class TelegramClientTracingTests
{
    private const string HttpDiagnosticListenerName = "HttpHandlerDiagnosticListener";
    private const string HttpActivitySourceName = "System.Net.Http";

    private static ServiceProvider Provider()
    {
        var services = new ServiceCollection();
        services.AddThemiaExceptionalAlerts(o =>
        {
            o.Enabled = true;
            o.BotToken = "123456:secret-token-value";
            o.ChatId = "-1001";
            o.AdminUrlTemplate = "https://admin.example.test/exceptions/{guid}";
        });
        return services.BuildServiceProvider();
    }

    private static HttpMessageHandler PrimaryHandler(ServiceProvider provider)
    {
        var handler = provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(TelegramAlertChannel.HttpClientName);
        while (handler is DelegatingHandler { InnerHandler: { } inner })
            handler = inner;
        return handler;
    }

    [Fact]
    public void TelegramClient_UsesAPrimaryHandlerWithoutHttpDiagnostics()
    {
        using var provider = Provider();

        var primary = Assert.IsType<SocketsHttpHandler>(PrimaryHandler(provider));
        Assert.Null(primary.ActivityHeadersPropagator);
    }

    [Fact]
    public async Task TelegramClient_EmitsNoHttpTelemetryCarryingTheRequestUrl()
    {
        using var server = new HttpListener();
        server.Prefixes.Add("http://127.0.0.1:18391/");
        server.Start();
        var serving = Task.Run(async () =>
        {
            var context = await server.GetContextAsync();
            context.Response.StatusCode = 200;
            context.Response.Close();
        });

        var recorded = 0;
        using var activities = new ActivityListener
        {
            ShouldListenTo = source => source.Name == HttpActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStarted = _ => Interlocked.Increment(ref recorded),
        };
        ActivitySource.AddActivityListener(activities);
        using var diagnostics = DiagnosticListener.AllListeners.Subscribe(new CountingObserver(() => Interlocked.Increment(ref recorded)));

        using var provider = Provider();
        using var invoker = new HttpClient(provider.GetRequiredService<IHttpMessageHandlerFactory>().CreateHandler(TelegramAlertChannel.HttpClientName));
        using var response = await invoker.GetAsync("http://127.0.0.1:18391/bot123456:secret-token-value/sendMessage");
        await serving;

        Assert.Equal(0, recorded);
    }

    private sealed class CountingObserver(Action onEvent) : IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>
    {
        public void OnNext(DiagnosticListener listener)
        {
            if (listener.Name == HttpDiagnosticListenerName)
                listener.Subscribe(this);
        }

        public void OnNext(KeyValuePair<string, object?> value) => onEvent();

        public void OnCompleted()
        {
        }

        public void OnError(Exception error)
        {
        }
    }
}
