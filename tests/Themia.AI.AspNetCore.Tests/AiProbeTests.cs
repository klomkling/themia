using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Themia.AI;
using Themia.AI.DependencyInjection;
using Xunit;

namespace Themia.AI.AspNetCore.Tests;

public class AiProbeTests
{
    private static async Task<(HttpClient Client, FakeAiCompletionProvider Provider)> ServerAsync(
        Action<AiProbeOptions>? configure,
        AiOutcome outcome = AiOutcome.Completed,
        string? text = "pong",
        AiUsage? usage = null,
        string? providerStatus = null)
    {
        var provider = new FakeAiCompletionProvider(
            AiProviderKeys.Gemini, outcome, text, usage, providerStatus);

        var host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(s =>
                {
                    s.AddRouting();
                    s.AddThemiaAi(o => o.Failover = [AiProviderKeys.Gemini]);
                    s.AddSingleton<IAiCompletionProvider>(provider);
                    s.Configure<AiProviderOptions>(AiProviderKeys.Gemini, po =>
                    {
                        po.CompletionModel = "completion-model";
                        po.TranslationModel = "translation-model";
                        po.Timeout = TimeSpan.FromSeconds(5);
                    });
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e => e.MapThemiaAiProbe("/ai/probe", configure));
                });
            })
            .StartAsync();

        return (host.GetTestClient(), provider);
    }

    /// <summary>
    /// Wires only the three services <c>MapThemiaAiProbe</c>'s GET route reads directly (the registered
    /// providers, <c>AiOptions</c>, and <c>AiProviderOptions</c>), bypassing <c>AddThemiaAi</c> and its
    /// <c>ValidateOnStart</c> validator entirely. Needed for
    /// <see cref="Get_report_flags_a_failover_entry_with_no_registered_provider"/>: a real host can never
    /// reach that state at startup (<c>AiOptionsValidator</c> refuses to start with a <c>Failover</c>
    /// entry naming an unregistered provider) — the state is only reachable after startup, if
    /// <c>AiOptions</c> is later reloaded from a hot-reloaded configuration source without re-validating.
    /// This constructs that post-reload state directly instead.
    /// </summary>
    private static async Task<HttpClient> UnvalidatedReportServerAsync(
        Action<AiProbeOptions>? configure, string[] failover, IAiCompletionProvider[] providers)
    {
        var host = await new HostBuilder()
            .ConfigureWebHost(web =>
            {
                web.UseTestServer();
                web.ConfigureServices(s =>
                {
                    s.AddRouting();
                    s.Configure<AiOptions>(o => o.Failover = failover);
                    foreach (var provider in providers)
                    {
                        s.AddSingleton<IAiCompletionProvider>(provider);
                        s.Configure<AiProviderOptions>(provider.Key, po =>
                        {
                            po.CompletionModel = "completion-model";
                            po.TranslationModel = "translation-model";
                            po.Timeout = TimeSpan.FromSeconds(5);
                        });
                    }
                });
                web.Configure(app =>
                {
                    app.UseRouting();
                    app.UseEndpoints(e => e.MapThemiaAiProbe("/ai/probe", configure));
                });
            })
            .StartAsync();

        return host.GetTestClient();
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    // ---- Fail-closed ----

    [Fact]
    public async Task Null_authorize_denies_the_get()
    {
        var (client, _) = await ServerAsync(configure: null);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/ai/probe")).StatusCode);
    }

    [Fact]
    public async Task Null_authorize_denies_the_post()
    {
        var (client, provider) = await ServerAsync(configure: null);
        var res = await client.PostAsync("/ai/probe", null);
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task Authorize_false_denies_the_get()
    {
        var (client, _) = await ServerAsync(o => o.Authorize = _ => Task.FromResult(false));
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/ai/probe")).StatusCode);
    }

    [Fact]
    public async Task Authorize_false_denies_the_post()
    {
        var (client, provider) = await ServerAsync(o => o.Authorize = _ => Task.FromResult(false));
        var res = await client.PostAsync("/ai/probe", null);
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task A_throwing_authorize_fails_closed_on_the_get()
    {
        var (client, _) = await ServerAsync(o => o.Authorize = _ => throw new InvalidOperationException());
        var res = await client.GetAsync("/ai/probe");
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
    }

    [Fact]
    public async Task A_throwing_authorize_fails_closed_on_the_post()
    {
        var (client, provider) = await ServerAsync(o => o.Authorize = _ => throw new InvalidOperationException());
        var res = await client.PostAsync("/ai/probe", null);
        Assert.Equal(HttpStatusCode.NotFound, res.StatusCode);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task Denied_response_is_not_cacheable()
    {
        var (client, _) = await ServerAsync(configure: null);
        var res = await client.GetAsync("/ai/probe");
        Assert.True(res.Headers.CacheControl!.NoStore);
    }

    // ---- GET: configuration report (no provider call) ----

    [Fact]
    public async Task Authorized_get_returns_200_with_the_configuration_report()
    {
        var (client, provider) = await ServerAsync(o => o.Authorize = _ => Task.FromResult(true));

        var res = await client.GetAsync("/ai/probe");

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(0, provider.Calls); // GET must never call a provider.
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        Assert.Contains(AiProviderKeys.Gemini, root.GetProperty("registeredProviders").EnumerateArray().Select(e => e.GetString()));

        var entry = root.GetProperty("failover").EnumerateArray().Single();
        Assert.Equal(AiProviderKeys.Gemini, entry.GetProperty("key").GetString());
        Assert.True(entry.GetProperty("registered").GetBoolean());
        Assert.Equal("completion-model", entry.GetProperty("completionModel").GetString());
        Assert.Equal("translation-model", entry.GetProperty("translationModel").GetString());
        Assert.Equal("00:00:05", entry.GetProperty("timeout").GetString());

        Assert.Equal(2, root.GetProperty("maxRetriesPerProvider").GetInt32());
        Assert.False(root.GetProperty("allowNoProvider").GetBoolean());
    }

    [Fact]
    public async Task Get_report_flags_a_failover_entry_with_no_registered_provider()
    {
        // AiOptionsValidator refuses a real host this state at startup; it is only reachable after a
        // hot configuration reload post-startup. See UnvalidatedReportServerAsync's remarks.
        var registered = new FakeAiCompletionProvider(AiProviderKeys.Gemini);
        var client = await UnvalidatedReportServerAsync(
            o => o.Authorize = _ => Task.FromResult(true),
            failover: [AiProviderKeys.Gemini, AiProviderKeys.OpenAiCompatible],
            providers: [registered]);

        var res = await client.GetAsync("/ai/probe");
        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var entries = doc.RootElement.GetProperty("failover").EnumerateArray().ToArray();

        Assert.True(entries.Single(e => e.GetProperty("key").GetString() == AiProviderKeys.Gemini).GetProperty("registered").GetBoolean());
        Assert.False(entries.Single(e => e.GetProperty("key").GetString() == AiProviderKeys.OpenAiCompatible).GetProperty("registered").GetBoolean());
    }

    [Fact]
    public async Task Get_response_is_not_cacheable()
    {
        var (client, _) = await ServerAsync(o => o.Authorize = _ => Task.FromResult(true));
        var res = await client.GetAsync("/ai/probe");
        Assert.True(res.Headers.CacheControl!.NoStore);
    }

    // ---- POST: exactly one real call ----

    [Fact]
    public async Task Authorized_post_with_no_body_defaults_to_completion_and_returns_the_call_report()
    {
        var (client, provider) = await ServerAsync(o => o.Authorize = _ => Task.FromResult(true), usage: new AiUsage(7, 11));

        var res = await client.PostAsync("/ai/probe", content: null);

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(1, provider.Calls);
        Assert.Equal("completion-model", provider.LastModel); // Completion is the default operation.

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal("Completed", root.GetProperty("outcome").GetString());
        Assert.Equal("completion-model", root.GetProperty("model").GetString());
        Assert.Equal(7, root.GetProperty("usage").GetProperty("inputTokens").GetInt32());
        Assert.Equal(11, root.GetProperty("usage").GetProperty("outputTokens").GetInt32());
        Assert.True(root.TryGetProperty("elapsed", out _));
    }

    [Fact]
    public async Task Post_with_translation_operation_selects_the_translation_model()
    {
        var (client, provider) = await ServerAsync(o => o.Authorize = _ => Task.FromResult(true));

        var res = await client.PostAsync("/ai/probe", Json("""{"operation":"Translation"}"""));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("translation-model", provider.LastModel);
    }

    [Fact]
    public async Task Post_ignores_a_caller_supplied_model_and_uses_the_configured_one()
    {
        var (client, provider) = await ServerAsync(o => o.Authorize = _ => Task.FromResult(true));

        var res = await client.PostAsync(
            "/ai/probe", Json("""{"operation":"Completion","model":"caller-chosen-model","endpoint":"https://evil.example"}"""));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal("completion-model", provider.LastModel);
    }

    [Fact]
    public async Task Post_rejects_an_unspecified_operation_without_calling_a_provider()
    {
        var (client, provider) = await ServerAsync(o => o.Authorize = _ => Task.FromResult(true));

        var res = await client.PostAsync("/ai/probe", Json("""{"operation":"Unspecified"}"""));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task Post_rejects_an_unknown_operation_without_calling_a_provider()
    {
        var (client, provider) = await ServerAsync(o => o.Authorize = _ => Task.FromResult(true));

        var res = await client.PostAsync("/ai/probe", Json("""{"operation":"Bogus"}"""));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task Post_rejects_an_over_length_prompt_without_calling_a_provider()
    {
        var (client, provider) = await ServerAsync(o => o.Authorize = _ => Task.FromResult(true));
        var overLong = new string('a', AiProbeEndpoints.MaxPromptLength + 1);

        var res = await client.PostAsync("/ai/probe", Json($$"""{"prompt":"{{overLong}}"}"""));

        Assert.Equal(HttpStatusCode.BadRequest, res.StatusCode);
        Assert.Equal(0, provider.Calls);
    }

    [Fact]
    public async Task Post_accepts_a_prompt_exactly_at_the_cap()
    {
        var (client, provider) = await ServerAsync(o => o.Authorize = _ => Task.FromResult(true));
        var atCap = new string('a', AiProbeEndpoints.MaxPromptLength);

        var res = await client.PostAsync("/ai/probe", Json($$"""{"prompt":"{{atCap}}"}"""));

        Assert.Equal(HttpStatusCode.OK, res.StatusCode);
        Assert.Equal(1, provider.Calls);
    }

    // Every ProviderStatus Themia's own code produces is a fixed literal or an HTTP status number —
    // "HTTP 401", "HTTP 429", "provider timeout", "total budget exhausted", "no provider was tried",
    // or a finishReason token. Without it all five of those answer AiOutcome.ProviderError and an
    // adopter cannot tell a wrong key from an exhausted quota from a too-low Timeout, which is the one
    // question this endpoint exists to answer.
    [Fact]
    public async Task Post_response_surfaces_the_providers_diagnostic_status()
    {
        var (client, _) = await ServerAsync(
            o => o.Authorize = _ => Task.FromResult(true), providerStatus: "HTTP 401");

        var res = await client.PostAsync("/ai/probe", content: null);

        var body = await res.Content.ReadAsStringAsync();
        Assert.Contains("HTTP 401", body, StringComparison.Ordinal);
    }

    // IAiCompletionProvider is a public seam, so a provider Themia did not write can put anything of any
    // length here. Truncation bounds the response; it is NOT a redaction and cannot be — a provider that
    // writes a secret into its own diagnostic has already handed it to the adopter's own logs. The cap
    // keeps one probe response from becoming an unbounded echo of provider prose.
    [Fact]
    public async Task A_long_provider_status_is_truncated_to_the_cap()
    {
        var (client, _) = await ServerAsync(
            o => o.Authorize = _ => Task.FromResult(true),
            providerStatus: new string('x', AiProbeEndpoints.MaxProviderStatusLength * 3));

        var res = await client.PostAsync("/ai/probe", content: null);

        using var body = JsonDocument.Parse(await res.Content.ReadAsStringAsync());
        var status = body.RootElement.GetProperty("providerStatus").GetString();
        Assert.Equal(AiProbeEndpoints.MaxProviderStatusLength, status!.Length);
    }

    [Fact]
    public async Task Post_response_is_not_cacheable()
    {
        var (client, _) = await ServerAsync(o => o.Authorize = _ => Task.FromResult(true));
        var res = await client.PostAsync("/ai/probe", content: null);
        Assert.True(res.Headers.CacheControl!.NoStore);
    }

    [Fact]
    public void MapThemiaAiProbe_returns_the_route_group()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Services.AddRouting();
        builder.Services.AddThemiaAi(o => { o.Failover = []; o.AllowNoProvider = true; });
        var app = builder.Build();

        var group = app.MapThemiaAiProbe();

        Assert.NotNull(group);
    }
}
