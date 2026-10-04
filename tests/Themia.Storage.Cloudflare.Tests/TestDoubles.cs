using System.Collections.Concurrent;
using System.Net;
using Microsoft.Extensions.Logging;

namespace Themia.Storage.Cloudflare.Tests;

/// <summary>What a stub handler saw, copied at send time (the request content is disposed afterwards).</summary>
internal sealed record SeenRequest(HttpMethod Method, Uri Uri, string? Authorization, string Body);

/// <summary>Answers every request with a fixed status and body, and records each request.</summary>
internal sealed class RecordingHandler(HttpStatusCode status, string body) : HttpMessageHandler
{
    public List<SeenRequest> Requests { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var content = request.Content is null ? string.Empty : await request.Content.ReadAsStringAsync(cancellationToken);
        Requests.Add(new SeenRequest(request.Method, request.RequestUri!, request.Headers.Authorization?.ToString(), content));
        return new HttpResponseMessage(status) { Content = new StringContent(body) };
    }
}

/// <summary>Fails the way a dead network does: no response at all.</summary>
internal sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw exception;
    }
}

/// <summary>Captures every message logged through it, across every category, including the framework's own HttpClient loggers.</summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentBag<string> messages = new();

    public IReadOnlyCollection<string> AllMessages => messages.ToArray();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, messages);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(string categoryName, ConcurrentBag<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            messages.Add($"[{categoryName}] {formatter(state, exception)}");
    }
}
