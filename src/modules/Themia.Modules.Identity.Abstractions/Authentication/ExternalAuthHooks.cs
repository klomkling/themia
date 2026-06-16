using Themia.Modules.Identity.Abstractions.Entities;

namespace Themia.Modules.Identity.Abstractions.Authentication;

/// <summary>Early gate before the provider exchange runs (rate-limit, provider allowlist).</summary>
/// <param name="provider">The requested provider key.</param>
public sealed class BeforeExternalLoginContext(string provider) : AuthenticationHookContext
{
    /// <summary>The requested provider key.</summary>
    public string Provider { get; } = provider;
}

/// <summary>Runs after the user is resolved/provisioned, before tokens are issued (post-auth gating).</summary>
/// <param name="user">The resolved or provisioned user.</param>
/// <param name="wasCreated">Whether a new user was provisioned.</param>
/// <param name="wasLinked">Whether a new link was created.</param>
public sealed class ExternalLoginSucceededContext(User user, bool wasCreated, bool wasLinked) : AuthenticationHookContext
{
    /// <summary>The resolved or provisioned user.</summary>
    public User User { get; } = user;

    /// <summary>Whether a new user was provisioned.</summary>
    public bool WasCreated { get; } = wasCreated;

    /// <summary>Whether a new link was created.</summary>
    public bool WasLinked { get; } = wasLinked;
}

/// <summary>Runs on any external-login failure with the real internal reason (audit only).</summary>
/// <remarks>Intentionally does not extend <see cref="AuthenticationHookContext"/> — an attempt that has
/// already failed cannot be denied; this context is audit-only.</remarks>
/// <param name="provider">The requested provider key.</param>
/// <param name="reason">The real internal outcome.</param>
public sealed class ExternalLoginFailedContext(string provider, ExternalLoginOutcome reason)
{
    /// <summary>The requested provider key.</summary>
    public string Provider { get; } = provider;

    /// <summary>The real internal outcome.</summary>
    public ExternalLoginOutcome Reason { get; } = reason;
}

/// <summary>Before/after extension points the default <see cref="IExternalAuthenticationFlow"/> invokes.
/// Separate from <see cref="IAuthenticationHooks"/> so adopters opt in only when using external login.
/// The default implementation is all no-ops; adopters override only what they need.</summary>
public interface IExternalAuthenticationHooks
{
    /// <summary>Early external-login gate, before the provider exchange.</summary>
    /// <param name="context">The before-external-login context.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task OnBeforeExternalLoginAsync(BeforeExternalLoginContext context, CancellationToken cancellationToken = default);

    /// <summary>After the user is resolved/provisioned, before tokens are issued.</summary>
    /// <param name="context">The external-login-succeeded context.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task OnExternalLoginSucceededAsync(ExternalLoginSucceededContext context, CancellationToken cancellationToken = default);

    /// <summary>On any external-login failure, with the real internal reason.</summary>
    /// <param name="context">The external-login-failed context.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    Task OnExternalLoginFailedAsync(ExternalLoginFailedContext context, CancellationToken cancellationToken = default);
}
