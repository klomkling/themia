namespace Themia.AspNetCore.Mapping;

/// <summary>Normalized mapping of a consumer exception to a ProblemDetails response. Produced by an
/// <see cref="IProblemMappable"/> exception or a registered mapper (<c>AddThemiaProblemMapping</c>).</summary>
/// <param name="Status">HTTP status code.</param>
/// <param name="ErrorCode">Optional machine-readable error code (emitted as the <c>errorCode</c> extension).</param>
/// <param name="Metadata">Optional extra key/values emitted as ProblemDetails extensions.</param>
/// <param name="ValidationPropertyName">When set, the response is a ValidationProblemDetails with an
/// <c>errors</c> dictionary keyed by this property name (use for 400 field errors).</param>
/// <param name="RetryAfterSeconds">When set, emits a <c>Retry-After</c> header and a <c>retryAfterSeconds</c> extension.</param>
public sealed record ProblemMapping(
    int Status,
    string? ErrorCode = null,
    IReadOnlyDictionary<string, object?>? Metadata = null,
    string? ValidationPropertyName = null,
    int? RetryAfterSeconds = null);
