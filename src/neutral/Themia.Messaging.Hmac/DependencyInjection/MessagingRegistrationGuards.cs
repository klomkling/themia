using Microsoft.Extensions.DependencyInjection;

namespace Themia.Messaging.DependencyInjection;

/// <summary>
/// The scan-and-throw registration guards the messaging <c>Add*</c> methods share. Internal: this is
/// framework plumbing, not adopter surface.
/// </summary>
/// <remarks>
/// The same <c>services.All(d =&gt; d.ServiceType != typeof(T))</c> shape had been copy-pasted across
/// eight call sites in five files. Each copy re-derived the scan, so any correction to the scan itself —
/// for instance excluding keyed registrations, which satisfy a <c>ServiceType</c> match and then fail
/// later with the opaque activation error these guards exist to prevent — had to be applied eight times
/// and would have been applied inconsistently. One definition, one place to fix.
/// </remarks>
internal static class MessagingRegistrationGuards
{
    /// <summary>Throws when no <typeparamref name="T"/> is registered yet.</summary>
    /// <typeparam name="T">The prerequisite service type.</typeparam>
    /// <param name="services">The service collection scanned so far.</param>
    /// <param name="message">The message explaining which call to make first, and why.</param>
    /// <exception cref="InvalidOperationException"><typeparamref name="T"/> is not registered.</exception>
    internal static void RequireRegistered<T>(IServiceCollection services, string message)
    {
        if (!IsRegistered<T>(services))
        {
            throw new InvalidOperationException(message);
        }
    }

    /// <summary>Throws when a <typeparamref name="T"/> is already registered.</summary>
    /// <typeparam name="T">The service type that must be registered exactly once.</typeparam>
    /// <param name="services">The service collection scanned so far.</param>
    /// <param name="message">The message explaining why a second registration is refused.</param>
    /// <exception cref="InvalidOperationException"><typeparamref name="T"/> is already registered.</exception>
    internal static void ThrowIfAlreadyRegistered<T>(IServiceCollection services, string message)
    {
        if (IsRegistered<T>(services))
        {
            throw new InvalidOperationException(message);
        }
    }

    // Matches on ServiceType rather than ImplementationInstance: a factory registration carries a null
    // instance, so an instance-scan would miss it and let a second descriptor be appended — after which
    // DI resolves the last one and two identities coexist with the later silently winning.
    private static bool IsRegistered<T>(IServiceCollection services)
    {
        for (var i = services.Count - 1; i >= 0; i--)
        {
            if (services[i].ServiceType == typeof(T))
            {
                return true;
            }
        }

        return false;
    }
}
