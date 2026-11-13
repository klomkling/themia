using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Http;
using Themia.Exceptional.Alerts;
using Xunit;

namespace Themia.Exceptional.Tests;

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class HttpTelemetryCollection
{
    public const string Name = "HttpTelemetryListeners";
}

// The listeners below are process-wide: nothing else may issue HTTP calls while they count.
[Collection(HttpTelemetryCollection.Name)]
public class TelegramClientTracingTests
{
    private const string HttpDiagnosticListenerName = "HttpHandlerDiagnosticListener";
    private const string HttpActivitySourceName = "System.Net.Http";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(10);

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
        Assert.False(primary.UseCookies);
    }

    [Fact]
    public async Task TelegramClient_EmitsNoHttpTelemetryCarryingTheRequestUrl()
    {
        var port = FreeLoopbackPort();
        using var server = new HttpListener();
        server.Prefixes.Add($"http://127.0.0.1:{port}/");
        server.Start();
        var serving = Task.Run(async () =>
        {
            var context = await server.GetContextAsync().WaitAsync(RequestTimeout);
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
        using var timeout = new CancellationTokenSource(RequestTimeout);
        using var response = await invoker.GetAsync(
            $"http://127.0.0.1:{port}/bot123456:secret-token-value/sendMessage", timeout.Token);
        await serving;

        Assert.Equal(0, recorded);
    }

    private static int FreeLoopbackPort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
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
