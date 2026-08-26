using Themia.Modules.Identity.Abstractions;

namespace Themia.Modules.Identity.Services;

/// <summary>
/// The default <see cref="IUserLifecycleHooks"/>: allows every mutation and observes none.
/// </summary>
/// <remarks>
/// Registered with <c>TryAdd</c>, so an adopter registering their own implementation first keeps it.
/// Every member of the interface has a default implementation, so this class declares none.
/// </remarks>
internal sealed class NoOpUserLifecycleHooks : IUserLifecycleHooks;
