using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Themia.AspNetCore.Exceptions;
using Themia.Modules.Identity.Abstractions;
using Themia.Modules.Identity.Abstractions.Authentication;

namespace Themia.Modules.Identity.AspNetCore.Endpoints;

/// <summary>Body of <c>otp/start</c>.</summary>
/// <param name="PhoneNumber">The phone to send a one-time code to.</param>
public sealed record OtpStartRequest(string PhoneNumber);

/// <summary>Response of <c>otp/start</c>. The same shape whether or not the phone has an account.</summary>
/// <param name="ChallengeId">Send back with the code to <c>otp/verify</c>.</param>
/// <param name="ExpiresAt">When the code stops being accepted.</param>
public sealed record OtpStartResponse(Guid ChallengeId, DateTimeOffset ExpiresAt);

/// <summary>Body of <c>otp/verify</c>.</summary>
/// <param name="ChallengeId">From <c>otp/start</c>.</param>
/// <param name="Code">The code the user received.</param>
public sealed record OtpVerifyRequest(Guid ChallengeId, string Code);

/// <summary>Maps the opt-in passwordless phone-code endpoints onto an <see cref="IEndpointRouteBuilder"/>.</summary>
public static class IdentityOtpEndpointRouteBuilderExtensions
{
    private const string GenericAuthFailure = "Invalid credentials.";

    /// <summary>
    /// Maps <c>POST otp/start</c> and <c>POST otp/verify</c> (typically under the same group as
    /// <c>MapIdentityAuthEndpoints</c>). Both are anonymous by nature, so attach a per-IP limit through
    /// <paramref name="configure"/>, for example <c>b =&gt; b.RequireRateLimiting("otp")</c>: the library
    /// throttles per phone and per challenge but cannot see the caller's address.
    /// </summary>
    /// <param name="endpoints">The route builder or group.</param>
    /// <param name="configure">Applied to both routes; <see langword="null"/> adds nothing.</param>
    /// <returns>The same builder.</returns>
    public static IEndpointRouteBuilder MapIdentityOtpEndpoints(
        this IEndpointRouteBuilder endpoints,
        Action<RouteHandlerBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        // Mapped first, then configured: `configure?.Invoke(endpoints.MapPost(...))` would skip the MapPost
        // itself whenever configure is null.
        var start = endpoints.MapPost("otp/start", StartAsync);
        var verify = endpoints.MapPost("otp/verify", VerifyAsync);
        configure?.Invoke(start);
        configure?.Invoke(verify);
        return endpoints;
    }

    private static async Task<IResult> StartAsync(
        OtpStartRequest request, IOtpLoginFlow flow, OtpLoginOptions options, CancellationToken cancellationToken)
    {
        if (request is null || string.IsNullOrWhiteSpace(request.PhoneNumber))
        {
            throw new ValidationException(nameof(OtpStartRequest), "Phone number is required.");
        }

        var result = await flow.StartAsync(request.PhoneNumber, cancellationToken).ConfigureAwait(false);
        if (!result.Issued)
        {
            // The same for a phone with and without an account. The hint is the resend cooldown; a window
            // cap can hold longer, in which case the client simply gets 429 again.
            throw new RateLimitException(
                "Too many codes requested for this number. Try again later.",
                Math.Max(1, (int)options.ResendCooldown.TotalSeconds));
        }

        return Results.Ok(new OtpStartResponse(result.ChallengeId, result.ExpiresAt));
    }

    private static async Task<IResult> VerifyAsync(OtpVerifyRequest request, IOtpLoginFlow flow, CancellationToken cancellationToken)
    {
        if (request is null || request.ChallengeId == Guid.Empty || string.IsNullOrWhiteSpace(request.Code))
        {
            throw new ValidationException(nameof(OtpVerifyRequest), "Challenge id and code are required.");
        }

        var result = await flow.VerifyAsync(request.ChallengeId, request.Code, cancellationToken).ConfigureAwait(false);
        if (!result.TryGetTokens(out var tokens))
        {
            // Wrong, expired, used, locked and denied all look the same from outside.
            throw new UnauthorizedException(GenericAuthFailure);
        }

        return Results.Ok(AuthResponse.FromTokens(tokens));
    }
}
