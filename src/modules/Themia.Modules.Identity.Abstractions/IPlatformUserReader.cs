using Themia.Modules.Identity.Abstractions.Entities;

namespace Themia.Modules.Identity.Abstractions;

/// <summary>Reads a platform (tenant-less) user by id regardless of the ambient tenant. <see cref="IUserService.FindByIdAsync"/>
/// is tenant-scoped and cannot see platform users, so the sign-in flows use this to re-read one after the success hook.
/// A service that does not implement it leaves the resolved copy in use.</summary>
/// <remarks>The flows test the registered <see cref="IUserService"/> instance with <c>is IPlatformUserReader</c>, so a decorator
/// or wrapper around <see cref="IUserService"/> hides the reader silently and platform users keep the pre-hook copy (a hook that
/// deactivates or locks a platform admin then does not stop that login). A wrapper must implement <see cref="IPlatformUserReader"/>
/// itself and forward to the inner service.</remarks>
public interface IPlatformUserReader
{
    /// <summary>Finds a platform user by id. Never returns a tenant's user.</summary>
    /// <param name="id">The user id.</param>
    /// <param name="cancellationToken">A cancellation token.</param>
    /// <returns>The platform user, or null.</returns>
    Task<User?> FindPlatformUserByIdAsync(Guid id, CancellationToken cancellationToken = default);
}
