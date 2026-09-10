using System.Collections.Concurrent;
using System.Net;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

using Themia.AI;
using Themia.AI.DependencyInjection;
using Themia.AI.Gemini.DependencyInjection;

using Xunit;

namespace Themia.AI.Gemini.Tests;

public sealed class GeminiCompletionProviderTests
{
    [Theory]
    [InlineData("Fixtures/gemini-completed.json", HttpStatusCode.OK, AiOutcome.Completed)]
    [InlineData("Fixtures/gemini-max-tokens.json", HttpStatusCode.OK, AiOutcome.Truncated)]
    [InlineData("Fixtures/gemini-filtered.json", HttpStatusCode.OK, AiOutcome.Filtered)]
    [InlineData("Fixtures/gemini-rate-limited.json", HttpStatusCode.TooManyRequests, AiOutcome.ProviderLimit)]
    [InlineData("Fixtures/gemini-error.json", HttpStatusCode.InternalServerError, AiOutcome.ProviderError)]
    public async Task Maps_each_captured_gemini_response(string fixture, HttpStatusCode status, AiOutcome expected)
    {
        var result = await ProviderReturning(fixture, status)
            .CompleteAsync(AiOperation.Completion, Build.Prompt(), default);

        Assert.Equal(expected, result.Outcome);
    }

    // A Filtered call consumed its input tokens and is billed for them. A caller recording cost from
    // successes alone undercounts most on the calls that failed.
    [Fact]
    public async Task Usage_is_reported_on_a_filtered_call()
    {
        var result = await ProviderReturning("Fixtures/gemini-filtered.json")
            .CompleteAsync(AiOperation.Completion, Build.Prompt(), default);

        Assert.Equal(AiOutcome.Filtered, result.Outcome);
        Assert.NotNull(result.Usage);
    }

    [Fact]
    public async Task A_length_stop_is_reported_as_truncated_not_completed()
        => Assert.Equal(AiOutcome.Truncated,
            (await ProviderReturning("Fixtures/gemini-max-tokens.json")
                .CompleteAsync(AiOperation.Completion, Build.Prompt(), default)).Outcome);

    [Fact]
    public async Task A_completed_result_carries_the_text()
    {
        var result = await ProviderReturning("Fixtures/gemini-completed.json")
            .CompleteAsync(AiOperation.Completion, Build.Prompt(), default);

        Assert.False(string.IsNullOrEmpty(result.Text));
    }

    [Fact]
    public async Task The_api_key_travels_as_a_header_and_never_in_the_request_uri()
    {
        // Google documents x-goog-api-key for the Generative Language REST API
        // (https://ai.google.dev/gemini-api/docs/api-key and the text-generation quickstart, both read
        // 2026-09-09). Sending it there instead of as ?key= keeps the secret out of the URI entirely,
        // which is what makes it absent from proxy logs, browser-style referrers and any HTTP log the
        // framework or a host writes. The sibling disclosure test below cannot establish this on its
        // own: it passes today for a reason unrelated to this package.
        var handler = new StubHandler(HttpStatusCode.OK, File.ReadAllText("Fixtures/gemini-completed.json"));

        await ClientOver(handler, apiKey: "SECRET-KEY-VALUE")
            .CompleteAsync(AiOperation.Completion, Build.Prompt(), default);

        Assert.DoesNotContain("SECRET-KEY-VALUE", handler.LastRequestUri, StringComparison.Ordinal);
        Assert.Equal("SECRET-KEY-VALUE", handler.LastApiKeyHeader);
    }

    // Writing no log statement of our own is NOT sufficient: AddHttpClient attaches
    // LoggingHttpMessageHandler, which logs the request URI and (at Trace) the request headers under
    // System.Net.Http.HttpClient.*. The handler that could leak the key is one nobody wrote, so this
    // captures EVERY category rather than just ILogger<GeminiCompletionProvider>.
    //
    // Measured 2026-09-09: this test passes even with the key back in the query string and with
    // RemoveAllLoggers() deleted, because Microsoft.Extensions.Http redacts both header values and the
    // URI query by default. It is a regression net over those defaults, not proof of anything this
    // package does. What this package actually does is send the key as a header at all — see
    // The_api_key_travels_as_a_header_and_never_in_the_request_uri, which does fail when that changes.
    [Fact]
    public async Task The_api_key_never_reaches_any_log_including_the_http_clients_own()
    {
        var capture = new CapturingLoggerProvider();
        var services = new ServiceCollection()
            .AddLogging(b => { b.AddProvider(capture); b.SetMinimumLevel(LogLevel.Trace); });
        services.AddThemiaAiGemini(o =>
        {
            o.ApiKey = "SECRET-KEY-VALUE";
            o.CompletionModel = "m";
            o.TranslationModel = "m";
        });
        services.AddThemiaAi(o => o.Failover = [AiProviderKeys.Gemini]);

        // A stub PRIMARY handler, so the request still travels through LoggingHttpMessageHandler — the
        // thing under test — while no packet leaves the machine.
        services.AddHttpClient(GeminiCompletionProvider.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(HttpStatusCode.Unauthorized, "{}"));

        using var sp = services.BuildServiceProvider();
        await sp.GetRequiredService<IAiCompletionClient>()
            .CompleteAsync(AiOperation.Completion, Build.Prompt(), default);

        Assert.DoesNotContain(capture.AllMessages, m => m.Contains("SECRET-KEY-VALUE", StringComparison.Ordinal));
    }


    // ---- Transport failure ----
    //
    // Every other handler in this suite answers with a status code, so nothing here could ever reach the
    // code path a real failure takes: a stopped server, a bad DNS name or a TLS error never produces a
    // response at all — HttpClient throws HttpRequestException. Before this test the exception escaped
    // CompleteAsync to the caller, so the dispatcher above never saw ProviderError and never retried or
    // failed over, and the whole call died at the first unreachable provider.
    [Fact]
    public async Task A_transport_failure_is_reported_as_provider_error_not_thrown()
    {
        var provider = ProviderOver(new ThrowingHandler(new HttpRequestException("Connection refused")));

        var result = await provider.CompleteAsync("test-model", Build.Prompt(), TimeSpan.FromSeconds(5), default);

        Assert.Equal(AiOutcome.ProviderError, result.Outcome);
    }

    // The other half of the same defect, and the one the class remarks already claimed to handle: a 200
    // whose body is not the JSON this parses — a gateway error page, or a body cut short — threw
    // JsonException straight past the mapping.
    [Fact]
    public async Task A_malformed_200_body_is_reported_as_provider_error_not_thrown()
    {
        var provider = ProviderOver(new StubHandler(HttpStatusCode.OK, "<html><body>502 Bad Gateway</body></html>"));

        var result = await provider.CompleteAsync("test-model", Build.Prompt(), TimeSpan.FromSeconds(5), default);

        Assert.Equal(AiOutcome.ProviderError, result.Outcome);
    }

    // The boundary the transport catch must not cross. A caller who stopped caring gets cancellation, not
    // a result: swallowing it here would turn every cancelled call into a retryable ProviderError and buy
    // the caller another round of provider calls they had just asked to stop.
    [Fact]
    public async Task Caller_cancellation_still_surfaces_as_cancellation()
    {
        var provider = ProviderOver(new ThrowingHandler(new HttpRequestException("Connection refused")));
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => provider.CompleteAsync("test-model", Build.Prompt(), TimeSpan.FromSeconds(5), cts.Token));
    }

    // Resolves the provider itself rather than the dispatcher above it, so a transport-failure assertion
    // reads the provider's own mapping instead of whatever the retry/failover policy made of it.
    private static IAiCompletionProvider ProviderOver(HttpMessageHandler handler)
    {
        var services = new ServiceCollection();
        services.AddThemiaAiGemini(o =>
        {
            o.ApiKey = "unused-test-key";
            o.CompletionModel = "test-model";
            o.TranslationModel = "test-model";
        });
        services.AddHttpClient(GeminiCompletionProvider.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => handler);

        return services.BuildServiceProvider().GetRequiredService<IAiCompletionProvider>();
    }

    // Builds a client over a stub primary handler returning the fixture body — no network call, so the
    // suite stays runnable without egress. The container is deliberately left undisposed: the returned
    // IAiCompletionClient is used after this method returns, and IHttpClientFactory's pooled handlers
    // must still be resolvable at that point.
    private static IAiCompletionClient ProviderReturning(string fixturePath, HttpStatusCode status = HttpStatusCode.OK)
        => ClientOver(new StubHandler(status, File.ReadAllText(fixturePath)));

    private static IAiCompletionClient ClientOver(StubHandler handler, string apiKey = "unused-test-key")
    {
        var services = new ServiceCollection();
        services.AddThemiaAiGemini(o =>
        {
            o.ApiKey = apiKey;
            o.CompletionModel = "test-model";
            o.TranslationModel = "test-model";
        });
        services.AddThemiaAi(o => o.Failover = [AiProviderKeys.Gemini]);
        services.AddHttpClient(GeminiCompletionProvider.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => handler);

        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IAiCompletionClient>();
    }
}

internal static class Build
{
    internal static AiPrompt Prompt(string user = "hello") => new() { User = user };
}

internal sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
{
    /// <summary>The URI of the last request, captured as a string — HttpClient disposes the request afterwards.</summary>
    internal string? LastRequestUri { get; private set; }

    /// <summary>The last request's <c>x-goog-api-key</c> header, or null when it carried none.</summary>
    internal string? LastApiKeyHeader { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequestUri = request.RequestUri?.ToString();
        LastApiKeyHeader = request.Headers.TryGetValues("x-goog-api-key", out var values)
            ? string.Join(',', values)
            : null;

        return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }
}

/// <summary>
/// An <see cref="ILoggerProvider"/> that captures every message logged through it, across every category
/// — including <c>System.Net.Http.HttpClient.*</c>, which nobody in this test suite wrote but
/// <c>AddHttpClient</c> attaches by default. Registering only <c>ILogger&lt;GeminiCompletionProvider&gt;</c>
/// would miss exactly the handler the key-disclosure test exists to catch.
/// </summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentBag<string> _messages = new();

    public IReadOnlyCollection<string> AllMessages => _messages.ToArray();

    public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, _messages);

    public void Dispose()
    {
    }

    private sealed class CapturingLogger(string categoryName, ConcurrentBag<string> messages) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull
            => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            messages.Add($"[{categoryName}] {formatter(state, exception)}");
        }
    }
}

/// <summary>
/// A handler that fails the way a real network failure does — no response at all. Every other stub in
/// this suite answers with a status code, which cannot reach the code path a stopped server, a bad DNS
/// name or a TLS error takes.
/// </summary>
internal sealed class ThrowingHandler(Exception exception) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        throw exception;
    }
}
