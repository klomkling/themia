using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Themia.Conversations.AspNetCore;

namespace Themia.Conversations.AspNetCore.Tests;

/// <summary>A TestServer host with fakes behind the conversation services. The member resolver reads <c>X-Test-Member: tenant:user</c>.</summary>
internal sealed class TestApp : IAsyncDisposable
{
    public const string Tenant = "tenant-a";
    public const string CsrfHeader = "X-Requested-With";

    private readonly WebApplication app;

    private TestApp(WebApplication app, HttpClient client, FakeConversationService service, FakeGuestAccess guests, LogCapture logs, BodyReadCounter bodyReads)
    {
        BodyReads = bodyReads;
        this.app = app;
        Client = client;
        Service = service;
        Guests = guests;
        Logs = logs;
    }

    public HttpClient Client { get; }

    public FakeConversationService Service { get; }

    public FakeGuestAccess Guests { get; }

    public LogCapture Logs { get; }

    /// <summary>How many times the server read the request body: the proof that a refused request was refused before its body was touched.</summary>
    public BodyReadCounter BodyReads { get; }

    public static async Task<TestApp> StartAsync(Action<ConversationHttpOptions>? configure = null, Action<IServiceCollection>? services = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        var service = new FakeConversationService();
        var guests = new FakeGuestAccess();
        var logs = new LogCapture();
        builder.Logging.ClearProviders();
        builder.Logging.AddProvider(logs);
        builder.Services.AddSingleton<IConversationService>(service);
        builder.Services.AddSingleton<IGuestAccess>(guests);
        builder.Services.AddSingleton<IConversationActorResolver, HeaderResolver>();
        builder.Services.Configure<ConversationOptions>(_ => { });
        builder.Services.AddThemiaConversationsHttp(configure);
        services?.Invoke(builder.Services);
        var app = builder.Build();
        var bodyReads = new BodyReadCounter();
        app.Use(async (context, next) =>
        {
            context.Request.Body = new CountingStream(context.Request.Body, bodyReads);
            await next();
        });
        app.MapThemiaConversations("/api/v1");
        await app.StartAsync();
        var client = app.GetTestClient();
        client.BaseAddress = new Uri("https://localhost");
        return new TestApp(app, client, service, guests, logs, bodyReads);
    }

    public static string Path(Guid id, string tail) => $"/api/v1/conversations/{id}/{tail}";

    public Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? json = null, bool csrf = true, string? member = null, string? cookie = null)
    {
        var request = new HttpRequestMessage(method, path);
        if (json is not null)
        {
            request.Content = new StringContent(json as string ?? JsonSerializer.Serialize(json), Encoding.UTF8, "application/json");
        }

        return SendAsync(request, csrf, member, cookie);
    }

    public Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, bool csrf = true, string? member = null, string? cookie = null)
    {
        if (csrf)
        {
            request.Headers.Add(CsrfHeader, "themia");
        }

        if (member is not null)
        {
            request.Headers.Add("X-Test-Member", member);
        }

        if (cookie is not null)
        {
            request.Headers.Add("Cookie", cookie);
        }

        return Client.SendAsync(request);
    }

    public async ValueTask DisposeAsync()
    {
        Client.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
    }

    private sealed class HeaderResolver : IConversationActorResolver
    {
        public ValueTask<ConversationCaller?> ResolveAsync(HttpContext context, CancellationToken cancellationToken)
        {
            if (!context.Request.Headers.TryGetValue("X-Test-Member", out var value) || value.Count == 0)
            {
                return ValueTask.FromResult<ConversationCaller?>(null);
            }

            var parts = value[0]!.Split(':');
            return ValueTask.FromResult<ConversationCaller?>(new ConversationCaller(parts[0], new ConversationActor(ConversationActorKind.Member, UserRef: parts[1])));
        }
    }
}

internal sealed class LogCapture : ILoggerProvider
{
    private readonly List<string> lines = [];

    public IReadOnlyList<string> Lines
    {
        get
        {
            lock (lines)
            {
                return [.. lines];
            }
        }
    }

    public ILogger CreateLogger(string categoryName) => new CaptureLogger(this);

    public void Dispose()
    {
    }

    private sealed class CaptureLogger(LogCapture owner) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (owner.lines)
            {
                owner.lines.Add(formatter(state, exception) + exception);
            }
        }
    }
}

internal sealed class BodyReadCounter
{
    private int reads;

    public int Count => Volatile.Read(ref reads);

    public void Add() => Interlocked.Increment(ref reads);
}

internal sealed class CountingStream(Stream inner, BodyReadCounter counter) : Stream
{
    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        counter.Add();
        return inner.Read(buffer, offset, count);
    }

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        counter.Add();
        return inner.ReadAsync(buffer, cancellationToken);
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        counter.Add();
        return inner.ReadAsync(buffer, offset, count, cancellationToken);
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
