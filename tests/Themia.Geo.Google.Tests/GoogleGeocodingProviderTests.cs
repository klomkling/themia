using System.Collections.Concurrent;
using System.Net;

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

using Themia.Geo;
using Themia.Geo.Google.DependencyInjection;

using Xunit;

namespace Themia.Geo.Google.Tests;

public sealed class GoogleGeocodingProviderTests
{
    private readonly CapturingLoggerProvider _capture = new();

    [Theory]
    [InlineData("google-ok.json", GeocodeOutcome.Found)]
    [InlineData("google-zero-results.json", GeocodeOutcome.NotFound)]
    [InlineData("google-over-query-limit.json", GeocodeOutcome.ProviderLimit)]
    [InlineData("google-error.json", GeocodeOutcome.ProviderError)]
    public async Task Maps_each_captured_google_status(string fixture, GeocodeOutcome expected)
    {
        var provider = ProviderReturning(await File.ReadAllTextAsync(Path.Combine("Fixtures", fixture)));
        var result = await provider.GeocodeAsync("anything", null, CancellationToken.None);
        Assert.Equal(expected, result.Outcome);
    }

    // ProviderLimit exists so a batch backfill stops instead of spending the rest of the day's allowance
    // retrying calls that cannot succeed. It only pays off if it is distinguishable.
    [Fact]
    public async Task Over_query_limit_is_not_reported_as_a_transient_error()
    {
        var provider = ProviderReturning(await File.ReadAllTextAsync("Fixtures/google-over-query-limit.json"));
        Assert.NotEqual(GeocodeOutcome.ProviderError, (await provider.GeocodeAsync("x", null, default)).Outcome);
    }

    [Fact]
    public async Task A_found_result_carries_the_coordinate()
    {
        var provider = ProviderReturning(await File.ReadAllTextAsync("Fixtures/google-ok.json"));
        var result = await provider.GeocodeAsync("x", null, default);
        Assert.NotNull(result.Point);
    }

    // Google takes the API key as a query parameter; there is no header form. Writing no log statement of
    // our own is NOT sufficient: AddHttpClient attaches LoggingHttpMessageHandler, which logs
    // "Sending HTTP request GET {uri}" at Information under System.Net.Http.HttpClient.*. The handler that
    // leaks the key is one nobody wrote, so a test capturing only our own logger passes while the key is
    // logged beside it.
    [Fact]
    public async Task The_api_key_never_reaches_any_log_including_the_http_clients_own()
    {
        // _capture registers for EVERY category, so it sees System.Net.Http.HttpClient.* too. Capturing only
        // ILogger<GoogleGeocodingProvider> would pass while the key is logged beside it by a handler nobody
        // wrote. The stub primary handler keeps the request off the network while still routing it through
        // LoggingHttpMessageHandler, which is the thing under test.
        var services = new ServiceCollection()
            .AddLogging(b => { b.AddProvider(_capture); b.SetMinimumLevel(LogLevel.Trace); });
        services.AddThemiaGeoGoogle(o => o.ApiKey = "SECRET-KEY-VALUE");
        services.AddHttpClient(GoogleGeocodingProvider.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(HttpStatusCode.OK, "{\"status\":\"ZERO_RESULTS\"}"));

        using var sp = services.BuildServiceProvider();
        await sp.GetRequiredService<IGeocodingProvider>().GeocodeAsync("x", null, default);

        Assert.DoesNotContain(_capture.AllMessages, m => m.Contains("SECRET-KEY-VALUE", StringComparison.Ordinal));
    }

    // What actually guards the production line, and the reason it exists.
    //
    // The first test alone is NOT enough: Microsoft.Extensions.Http redacts the query out of its
    // request-URI log by default (LoggingHttpMessageHandler -> LogHelper.LogRequestStart ->
    // UriRedactionHelper), so it passes even with RemoveAllLoggers() deleted. That default is a package
    // behaviour we do not own and it is switchable process-wide via
    // DOTNET_SYSTEM_NET_HTTP_DISABLEURIREDACTION — a plausible thing for an operator to set while
    // debugging some other HTTP problem, at which point this key starts appearing in the host's logs.
    //
    // So assert what RemoveAllLoggers() does: the framework writes NOTHING for this client — not a
    // redacted line, nothing. Deleting that call makes a "Sending HTTP request" line appear and this
    // test go red, which is what the first test cannot do.
    //
    // Asserted behaviourally rather than by reading a flag: RemoveAllLoggers() works through internal
    // filter state, and HttpClientFactoryOptions exposes no public property to check.
    //
    // Deliberately NOT written by flipping the redaction AppContext switch. That was tried; the switch
    // is read once and cached, so such a test passes alone and fails after any earlier test has logged
    // an HTTP request — and it only worked at all *because* the suppression kept those earlier tests
    // silent, which is circular.
    [Fact]
    public async Task The_geocoding_client_emits_no_framework_http_logs_at_all()
    {
        var capture = new CapturingLoggerProvider();
        var services = new ServiceCollection()
            .AddLogging(b => { b.AddProvider(capture); b.SetMinimumLevel(LogLevel.Trace); });
        services.AddThemiaGeoGoogle(o => o.ApiKey = "SECRET-KEY-VALUE");
        services.AddHttpClient(GoogleGeocodingProvider.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(HttpStatusCode.OK, "{\"status\":\"ZERO_RESULTS\"}"));

        using var sp = services.BuildServiceProvider();
        await sp.GetRequiredService<IGeocodingProvider>().GeocodeAsync("x", null, default);

        Assert.DoesNotContain(capture.AllMessages, m => m.Contains("Sending HTTP request", StringComparison.Ordinal));
    }

    // Builds a provider over a stub primary handler returning the fixture body — no network call, so the
    // suite stays runnable without egress. The container is deliberately left undisposed: the returned
    // IGeocodingProvider is used after this method returns, and IHttpClientFactory's pooled handlers must
    // still be resolvable at that point.
    private static IGeocodingProvider ProviderReturning(string fixtureBody)
    {
        var services = new ServiceCollection();
        services.AddThemiaGeoGoogle(o => o.ApiKey = "unused-test-key");
        services.AddHttpClient(GoogleGeocodingProvider.HttpClientName)
            .ConfigurePrimaryHttpMessageHandler(() => new StubHandler(HttpStatusCode.OK, fixtureBody));

        var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IGeocodingProvider>();
    }
}

internal sealed class StubHandler(HttpStatusCode status, string body) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
}

/// <summary>
/// An <see cref="ILoggerProvider"/> that captures every message logged through it, across every category
/// — including <c>System.Net.Http.HttpClient.*</c>, which nobody in this test suite wrote but
/// <c>AddHttpClient</c> attaches by default. Registering only <c>ILogger&lt;GoogleGeocodingProvider&gt;</c>
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
