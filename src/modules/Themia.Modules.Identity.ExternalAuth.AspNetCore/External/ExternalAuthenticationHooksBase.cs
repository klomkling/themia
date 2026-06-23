using Themia.Modules.Identity.Abstractions.Authentication;

namespace Themia.Modules.Identity.ExternalAuth.AspNetCore.External;

/// <summary>No-op <see cref="IExternalAuthenticationHooks"/>. Registered by default via <c>TryAdd</c>;
/// adopters subclass and override only the hooks they need.</summary>
public class ExternalAuthenticationHooksBase : IExternalAuthenticationHooks
{
    /// <inheritdoc />
    public virtual Task OnBeforeExternalLoginAsync(BeforeExternalLoginContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc />
    public virtual Task OnExternalLoginSucceededAsync(ExternalLoginSucceededContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;

    /// <inheritdoc />
    public virtual Task OnExternalLoginFailedAsync(ExternalLoginFailedContext context, CancellationToken cancellationToken = default) => Task.CompletedTask;
}
