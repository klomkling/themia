using System.Collections.Concurrent;
using System.Net;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging;

using Themia.AI;
using Themia.AI.DependencyInjection;
using Themia.AI.OpenAiCompatible.DependencyInjection;

using Xunit;

namespace Themia.AI.OpenAiCompatible.Tests;

public sealed class OpenAiCompatibleCompletionProviderTests
{
    [Theory]
    [InlineData("Fixtures/openai-completed.json", HttpStatusCode.OK, AiOutcome.Completed)]
    [InlineData("Fixtures/openai-max-tokens.json", HttpStatusCode.OK, AiOutcome.Truncated)]
    [InlineData("Fixtures/openai-filtered.json", HttpStatusCode.OK, AiOutcome.Filtered)]
    [InlineData("Fixtures/openai-rate-limited.json", HttpStatusCode.TooManyRequests, AiOutcome.ProviderLimit)]
    [InlineData("Fixtures/openai-error.json", HttpStatusCode.InternalServerError, AiOutcome.ProviderError)]
    public async Task Maps_each_captured_openai_compatible_response(string fixture, HttpStatusCode status, AiOutcome expected)
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
        var result = await ProviderReturning("Fixtures/openai-filtered.json")
            .CompleteAsync(AiOperation.Completion, Build.Prompt(), default);

        Assert.Equal(AiOutcome.Filtered, result.Outcome);
        Assert.NotNull(result.Usage);
    }

    [Fact]
    public async Task A_length_stop_is_reported_as_truncated_not_completed()
        => Assert.Equal(AiOutcome.Truncated,
            (await ProviderReturning("Fixtures/openai-max-tokens.json")
                .CompleteAsync(AiOperation.Completion, Build.Prompt(), default)).Outcome);

    [Fact]
    public async Task A_completed_result_carries_the_text()
    {
        var result = await ProviderReturning("Fixtures/openai-completed.json")
            .CompleteAsync(AiOperation.Completion, Build.Prompt(), default);

        Assert.False(string.IsNullOrEmpty(result.Text));
    }

    [Fact]
    public async Task The_api_key_travels_as_a_bearer_header_and_never_in_the_request_uri()
    {
        // The OpenAI chat-completions API documents Authorization: Bearer <key> for every compatible
        // endpoint (OpenAI, Groq, Cerebras, LM Studio, vLLM); Ollama accepts and ignores it. Sending it
        // as a header rather than a query parameter keeps the secret out of the URI entirely, which is
        // what makes it absent from proxy logs, browser-style referrers and any HTTP log the framework
        // or a host writes. The sibling disclosure test below cannot establish this on its own: it
        // passes today for a reason unrelated to this package.
        var handler = new StubHandler(HttpStatusCode.OK, File.ReadAllText("Fixtures/openai-completed.json"));

        await ClientOver(handler, apiKey: "SECRET-KEY-VALUE")
            .CompleteAsync(AiOperation.Completion, Build.Prompt(), default);

        Assert.DoesNotContain("SECRET-KEY-VALUE", handler.LastRequestUri, StringComparison.Ordinal);
        Assert.Equal("Bearer SECRET-KEY-VALUE", handler.LastAuthorizationHeader);
    }

    // A Bearer token on a plaintext connection is readable by every hop between here and the endpoint.
    // http is legitimate for this provider — Ollama, LM Studio and vLLM all serve it on loopback — so the
    // guard is not "https only"; it is "no CREDENTIAL over plaintext to somewhere off this machine".
    // Without it the misconfiguration is invisible: the calls succeed, and the key is on the wire.
    [Fact]
    public void Rejects_a_bearer_key_sent_over_plaintext_http_to_a_remote_host()
    {
        var ex = Assert.Throws<OptionsValidationException>(() => StartHost(o =>
        {
            o.BaseUrl = new Uri("http://api.example.com/v1");
            o.ApiKey = "SECRET-KEY-VALUE";
        }));

        Assert.Contains(nameof(OpenAiCompatibleOptions.ApiKey), ex.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("SECRET-KEY-VALUE", ex.Message, StringComparison.Ordinal);
    }

    // The two configurations that must keep working: a local endpoint with no auth at all (the free /
    // self-hosted case this provider exists for), and a key against loopback, where nothing leaves the
    // machine to be intercepted.
    [Theory]
    [InlineData("http://localhost:11434/v1", "")]
    [InlineData("http://127.0.0.1:11434/v1", "local-dev-key")]
    [InlineData("https://api.example.com/v1", "SECRET-KEY-VALUE")]
    public void Accepts_plaintext_on_loopback_and_a_key_over_https(string baseUrl, string apiKey)
    {
        var ex = Record.Exception(() => StartHost(o =>
        {
            o.BaseUrl = new Uri(baseUrl);
            o.ApiKey = apiKey;
        }));

        Assert.Null(ex);
    }

    // Resolving IOptions<T> is what runs a ValidateOnStart rule outside a real host.
    private static void StartHost(Action<OpenAiCompatibleOptions> configure)
    {
        var services = new ServiceCollection();
        services.AddThemiaAiOpenAiCompatible(o =>
        {
            o.CompletionModel = "test-model";
            o.TranslationModel = "test-model";
            configure(o);
        });

        using var provider = services.BuildServiceProvider();
        _ = provider.GetRequiredService<IOptions<OpenAiCompatibleOptions>>().Value;
    }

    // Writing no log statement of our own is NOT sufficient: AddHttpClient attaches
    // LoggingHttpMessageHandler, which logs the request URI and (at Trace) the request headers under
    // System.Net.Http.HttpClient.*. The handler that could leak the key is one nobody wrote, so this
    // captures EVERY category rather than just ILogger<OpenAiCompatibleCompletionProvider>.
    //
    // The key here is a bearer header, not a query parameter, so the URI was never the leak — but this
    // asserts it anyway, because the header could still be logged by a handler configured elsewhere.
    [Fact]
    public async Task The_api_key_never_reaches_any_log_including_the_http_clients_own()
    {
        var capture = new CapturingLoggerProvider();
        var services = new ServiceCollection()
            .AddLogging(b => { b.AddProvider(capture); b.SetMinimumLevel(LogLevel.Trace); });
        services.AddThemiaAiOpenAiCompatible(o =>
        {
            o.BaseUrl = new Uri("http://localhost:11434/v1");
            o.ApiKey = "SECRET-KEY-VALUE";
            o.CompletionModel = "m";
            o.TranslationModel = "m";
        });
        services.AddThemiaAi(o => o.Failover = [AiProviderKeys.OpenAiCompatible]);

        // A stub PRIMARY handler, so the request still travels through LoggingHttpMessageHandler — the
        // thing under test — while no packet leaves the machine.
        services.AddHttpClient(OpenAiCompatibleCompletionProvider.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(HttpStatusCode.Unauthorized, "{}"));

        using var sp = services.BuildServiceProvider();
        await sp.GetRequiredService<IAiCompletionClient>()
            .CompleteAsync(AiOperation.Completion, Build.Prompt(), default);

        Assert.DoesNotContain(capture.AllMessages, m => m.Contains("SECRET-KEY-VALUE", StringComparison.Ordinal));
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
        services.AddThemiaAiOpenAiCompatible(o =>
        {
            o.BaseUrl = new Uri("http://localhost:11434/v1");
            o.ApiKey = apiKey;
            o.CompletionModel = "test-model";
            o.TranslationModel = "test-model";
        });
        services.AddThemiaAi(o => o.Failover = [AiProviderKeys.OpenAiCompatible]);
        services.AddHttpClient(OpenAiCompatibleCompletionProvider.HttpClientName)
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

    /// <summary>The last request's <c>Authorization</c> header value, or null when it carried none.</summary>
    internal string? LastAuthorizationHeader { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        LastRequestUri = request.RequestUri?.ToString();
        LastAuthorizationHeader = request.Headers.Authorization?.ToString();

        return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
    }
}

/// <summary>
/// An <see cref="ILoggerProvider"/> that captures every message logged through it, across every category
/// — including <c>System.Net.Http.HttpClient.*</c>, which nobody in this test suite wrote but
/// <c>AddHttpClient</c> attaches by default. Registering only
/// <c>ILogger&lt;OpenAiCompatibleCompletionProvider&gt;</c> would miss exactly the handler the
/// key-disclosure test exists to catch.
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
