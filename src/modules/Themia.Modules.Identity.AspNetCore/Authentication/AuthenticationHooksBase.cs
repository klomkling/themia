using Themia.Modules.Identity.Abstractions.Authentication;

namespace Themia.Modules.Identity.AspNetCore.Authentication;

/// <summary>No-op <see cref="IAuthenticationHooks"/>. Registered by default via <c>TryAdd</c>; adopters
/// subclass and override only the hooks they need.</summary>
public class AuthenticationHooksBase : IAuthenticationHooks
{
    /// <inheritdoc />
    public virtual Task OnBeforeLoginAsync(BeforeLoginContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc />
    public virtual Task OnLoginSucceededAsync(LoginSucceededContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc />
    public virtual Task OnLoginFailedAsync(LoginFailedContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc />
    public virtual Task OnBeforeRefreshAsync(BeforeRefreshContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc />
    public virtual Task OnRefreshSucceededAsync(RefreshSucceededContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc />
    public virtual Task OnLogoutAsync(LogoutContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
