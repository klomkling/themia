using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Themia.AI.AspNetCore;

/// <summary>
/// Mounts the AI provider probe an adopter uses to verify, against their own deployed configuration,
/// that their AI provider setup actually works. Themia never holds a key and ships no deployable of its
/// own — this is mounted in the adopter's own app, reading whatever they configured through
/// <c>AddThemiaAi</c> / a provider package's <c>AddThemiaAiX</c>.
/// </summary>
public static class AiProbeEndpoints
{
    private const string LoggerCategory = "Themia.AI.AspNetCore";

    /// <summary>Caps the caller-supplied <c>POST</c> prompt text. Past the adopter's own auth, an
    /// uncapped probe is a free AI proxy that bills the adopter per call — this exists to bound that,
    /// not to bound legitimate prompt size.</summary>
    public const int MaxPromptLength = 500;

    /// <summary>The cap applied to <see cref="AiCompletion.ProviderStatus"/> before it is reported.
    /// A bound on response size, not a redaction — see <see cref="AiProbeCallReport"/>.</summary>
    public const int MaxProviderStatusLength = 200;

    private const string DefaultProbePrompt = "Respond with a single short sentence confirming you received this message.";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    /// <summary>
    /// Maps the probe's two routes and returns the route group. <c>GET {path}</c> reports the resolved
    /// configuration and makes no provider call. <c>POST {path}</c> makes exactly one real call through
    /// <see cref="IAiCompletionClient"/> and reports the result. Both routes are governed by
    /// <see cref="AiProbeOptions.Authorize"/> (fail-closed when unset).
    /// </summary>
    /// <param name="endpoints">The endpoint route builder.</param>
    /// <param name="path">Route (default <c>/ai/probe</c>).</param>
    /// <param name="configure">Optional configuration callback.</param>
    /// <returns>The route group for further configuration.</returns>
    public static RouteGroupBuilder MapThemiaAiProbe(
        this IEndpointRouteBuilder endpoints,
        string path = "/ai/probe",
        Action<AiProbeOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var options = new AiProbeOptions();
        configure?.Invoke(options);

        if (options.Authorize is null)
        {
            var logger = endpoints.ServiceProvider.GetService<ILoggerFactory>()?.CreateLogger(LoggerCategory);
            logger?.LogWarning(
                "AI probe mounted at {Path} without an Authorize predicate; all requests are denied.", path);
        }

        var group = endpoints.MapGroup(path);

        group.MapGet("", (
            HttpContext ctx,
            IEnumerable<IAiCompletionProvider> providers,
            IOptions<AiOptions> aiOptions,
            IOptionsMonitor<AiProviderOptions> providerOptions,
            CancellationToken ct) =>
            HandleReportAsync(ctx, providers, aiOptions.Value, providerOptions, options, ct));

        group.MapPost("", (
            HttpContext ctx,
            IAiCompletionClient client,
            CancellationToken ct) =>
            HandleProbeAsync(ctx, client, options, ct));

        return group;
    }

    private static async Task HandleReportAsync(
        HttpContext ctx,
        IEnumerable<IAiCompletionProvider> providers,
        AiOptions aiOptions,
        IOptionsMonitor<AiProviderOptions> providerOptions,
        AiProbeOptions options,
        CancellationToken ct)
    {
        if (!await AuthorizedAsync(ctx, options).ConfigureAwait(false)) { Deny(ctx); return; }

        var registeredKeys = providers.Select(p => p.Key).ToArray();
        var registeredSet = new HashSet<string>(registeredKeys, StringComparer.OrdinalIgnoreCase);

        var failover = aiOptions.Failover
            .Select(key =>
            {
                var entry = providerOptions.Get(key);
                return new AiProbeFailoverEntry(
                    key, registeredSet.Contains(key), entry.CompletionModel, entry.TranslationModel, entry.Timeout);
            })
            .ToArray();

        var report = new AiProbeConfigurationReport(
            registeredKeys, failover, aiOptions.MaxRetriesPerProvider, aiOptions.TotalBudget, aiOptions.AllowNoProvider);

        await WriteJsonAsync(ctx, StatusCodes.Status200OK, report, ct).ConfigureAwait(false);
    }

    private static async Task HandleProbeAsync(
        HttpContext ctx, IAiCompletionClient client, AiProbeOptions options, CancellationToken ct)
    {
        if (!await AuthorizedAsync(ctx, options).ConfigureAwait(false)) { Deny(ctx); return; }

        AiProbeRequestBody? body = null;
        if (ctx.Request.ContentLength is > 0)
        {
            try
            {
                body = await JsonSerializer.DeserializeAsync<AiProbeRequestBody>(ctx.Request.Body, JsonOptions, ct)
                    .ConfigureAwait(false);
            }
            catch (JsonException)
            {
                await WriteProblemAsync(ctx, "The request body is not valid JSON.", ct).ConfigureAwait(false);
                return;
            }
        }

        if (!TryParseOperation(body?.Operation, out var operation))
        {
            await WriteProblemAsync(
                ctx,
                $"'{body?.Operation}' is not a known AiOperation. Use '{nameof(AiOperation.Completion)}' or " +
                $"'{nameof(AiOperation.Translation)}', or omit the field to default to " +
                $"'{nameof(AiOperation.Completion)}'.",
                ct).ConfigureAwait(false);
            return;
        }

        var prompt = body?.Prompt ?? DefaultProbePrompt;
        if (prompt.Length > MaxPromptLength)
        {
            await WriteProblemAsync(
                ctx, $"The probe prompt is capped at {MaxPromptLength} characters.", ct).ConfigureAwait(false);
            return;
        }

        var stopwatch = Stopwatch.StartNew();
        var completion = await client.CompleteAsync(operation, new AiPrompt { User = prompt }, ct).ConfigureAwait(false);
        stopwatch.Stop();

        var report = new AiProbeCallReport(
            completion.Outcome, completion.Model, completion.Usage, stopwatch.Elapsed,
            Truncate(completion.ProviderStatus));
        await WriteJsonAsync(ctx, StatusCodes.Status200OK, report, ct).ConfigureAwait(false);
    }

    // Missing/omitted -> Completion (the sensible default for a bare POST). Present but Unspecified,
    // unrecognised, or out of the enum's defined range -> rejected: the caller may pick which of the
    // adopter's two configured models answers, and nothing else.
    private static bool TryParseOperation(string? raw, out AiOperation operation)
    {
        if (raw is null)
        {
            operation = AiOperation.Completion;
            return true;
        }

        if (Enum.TryParse<AiOperation>(raw, ignoreCase: true, out var parsed)
            && Enum.IsDefined(parsed)
            && parsed != AiOperation.Unspecified)
        {
            operation = parsed;
            return true;
        }

        operation = default;
        return false;
    }

    private static async Task<bool> AuthorizedAsync(HttpContext ctx, AiProbeOptions options)
    {
        if (options.Authorize is null)
        {
            return false;
        }

        try
        {
            return await options.Authorize(ctx).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A throwing Authorize predicate must fail closed — deny, never 500 or serve data.
            ctx.RequestServices.GetService<ILoggerFactory>()?
                .CreateLogger(LoggerCategory)
                .LogError(ex, "AI probe Authorize predicate threw; denying request.");
            return false;
        }
    }

    // Route-hiding 404, matching Themia.Audit.AspNetCore: an unauthenticated caller cannot distinguish
    // "not mounted" from "mounted but denied".
    private static void Deny(HttpContext ctx)
    {
        PreventCaching(ctx.Response);
        ctx.Response.StatusCode = StatusCodes.Status404NotFound;
    }

    private static Task WriteProblemAsync(HttpContext ctx, string detail, CancellationToken ct)
    {
        var problem = new ProblemDetails
        {
            Status = StatusCodes.Status400BadRequest,
            Title = "Invalid probe request",
            Detail = detail,
        };
        ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
        PreventCaching(ctx.Response);
        ctx.Response.ContentType = "application/problem+json; charset=utf-8";
        return JsonSerializer.SerializeAsync(ctx.Response.Body, problem, JsonOptions, ct);
    }

    private static string? Truncate(string? providerStatus)
        => providerStatus is { Length: > MaxProviderStatusLength }
            ? providerStatus[..MaxProviderStatusLength]
            : providerStatus;

    private static Task WriteJsonAsync<T>(HttpContext ctx, int statusCode, T value, CancellationToken ct)
    {
        ctx.Response.StatusCode = statusCode;
        PreventCaching(ctx.Response);
        ctx.Response.ContentType = "application/json; charset=utf-8";
        return JsonSerializer.SerializeAsync(ctx.Response.Body, value, JsonOptions, ct);
    }

    // A probe result must never be cached: it answers "does it work right now", and a shared cache
    // serving a stale 200 (or a stale deny) back to any caller defeats the point of probing live.
    private static void PreventCaching(HttpResponse response)
    {
        response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        response.Headers.Pragma = "no-cache";
        response.Headers.AppendCommaSeparatedValues("Vary", "Cookie", "Authorization");
    }

    private sealed class AiProbeRequestBody
    {
        public string? Operation { get; set; }

        public string? Prompt { get; set; }
    }
}
