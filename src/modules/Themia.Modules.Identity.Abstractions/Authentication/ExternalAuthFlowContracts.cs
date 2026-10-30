namespace Themia.Modules.Identity.Abstractions.Authentication;

/// <summary>The outcome of an external-login attempt. Non-success collapses to a uniform 401 at the
/// boundary (except ProviderNotFound → 404).</summary>
public enum ExternalLoginOutcome
{
    /// <summary>Authenticated; tokens issued.</summary>
    Success,

    /// <summary>No provider registered under the requested name.</summary>
    ProviderNotFound,

    /// <summary>The provider rejected the code or the identity failed validation.</summary>
    ProviderRejected,

    /// <summary>A hook denied the attempt.</summary>
    Denied,

    /// <summary>The resolved (pre-existing) linked account is inactive or locked out. Collapses to the
    /// same uniform 401 as the other failures; the distinct value exists only so audit sees the reason.</summary>
    AccountInactive,

    /// <summary>The provider accepted the identity but the app's <see cref="ISecondFactorPolicy"/> requires a second factor first: nothing was
    /// issued, and this is not a failed login. See <see cref="ExternalLoginFlowResult.SecondFactor"/>.</summary>
    SecondFactorRequired,
}

/// <summary>The result of <see cref="IExternalAuthenticationFlow.AuthenticateAsync"/>.</summary>
public readonly record struct ExternalLoginFlowResult
{
    private ExternalLoginFlowResult(ExternalLoginOutcome outcome, AuthTokens? tokens, bool wasCreated, bool wasLinked, PendingSecondFactor? secondFactor = null)
    {
        Outcome = outcome; Tokens = tokens; WasCreated = wasCreated; WasLinked = wasLinked; SecondFactor = secondFactor;
    }

    /// <summary>The outcome.</summary>
    public ExternalLoginOutcome Outcome { get; }

    /// <summary>The issued tokens on success; otherwise null. (Reuses 0.5.1 <see cref="AuthTokens"/>.)</summary>
    public AuthTokens? Tokens { get; }

    /// <summary>Whether a new user was provisioned.</summary>
    public bool WasCreated { get; }

    /// <summary>Whether a new link was created.</summary>
    public bool WasLinked { get; }

    /// <summary>The pending second factor when <see cref="Outcome"/> is <see cref="ExternalLoginOutcome.SecondFactorRequired"/>; otherwise null.</summary>
    public PendingSecondFactor? SecondFactor { get; }

    /// <summary>Whether the attempt succeeded.</summary>
    public bool Succeeded => Outcome == ExternalLoginOutcome.Success;

    /// <summary>Creates a success result.</summary>
    /// <param name="tokens">The issued access + refresh pair.</param>
    /// <param name="created">Whether a new user was provisioned.</param>
    /// <param name="linked">Whether a new link was created.</param>
    public static ExternalLoginFlowResult Success(AuthTokens tokens, bool created, bool linked) =>
        new(ExternalLoginOutcome.Success, tokens, created, linked);

    /// <summary>Creates a provider-not-found result.</summary>
    public static ExternalLoginFlowResult ProviderNotFound() => new(ExternalLoginOutcome.ProviderNotFound, null, false, false);

    /// <summary>Creates a provider-rejected result.</summary>
    public static ExternalLoginFlowResult ProviderRejected() => new(ExternalLoginOutcome.ProviderRejected, null, false, false);

    /// <summary>Creates a denied result.</summary>
    public static ExternalLoginFlowResult Denied() => new(ExternalLoginOutcome.Denied, null, false, false);

    /// <summary>Creates a result for a login that must pass a second factor before anything is issued.</summary>
    /// <param name="pending">The pending login and its ticket.</param>
    /// <param name="created">Whether a new user was provisioned.</param>
    /// <param name="linked">Whether a new link was created.</param>
    public static ExternalLoginFlowResult RequiresSecondFactor(PendingSecondFactor pending, bool created, bool linked) =>
        new(ExternalLoginOutcome.SecondFactorRequired, null, created, linked, pending);

    /// <summary>Creates an account-inactive result (resolved account is disabled or locked out).</summary>
    public static ExternalLoginFlowResult AccountInactive() => new(ExternalLoginOutcome.AccountInactive, null, false, false);
}

/// <summary>Orchestrates the external-login sequence (provider exchange → link/provision → issue
/// tokens). Default impl lives in Themia.Modules.Identity.AspNetCore; replaceable via DI.</summary>
public interface IExternalAuthenticationFlow
{
    /// <summary>Authenticates via the named provider and issues an access + refresh pair.</summary>
    /// <param name="provider">The registered provider key.</param>
    /// <param name="request">The external-login request.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The external-login result.</returns>
    Task<ExternalLoginFlowResult> AuthenticateAsync(string provider, ExternalAuthRequest request, CancellationToken cancellationToken = default);
}
