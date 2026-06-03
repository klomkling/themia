using Themia.Generators.Abstractions.Validation;

namespace Themia.Generators.Abstractions.Scanning;

/// <summary>
/// Represents a resolved service registration pairing an implementation type
/// with the service type it fulfils and the lifetime it is registered under.
/// </summary>
public sealed class ResolvedServiceType
{
    /// <summary>Gets the fully qualified name of the implementation type.</summary>
    public string ImplementationType { get; }

    /// <summary>Gets the fully qualified name of the service type (interface or concrete).</summary>
    public string ServiceType { get; }

    /// <summary>Gets the DI lifetime for this registration.</summary>
    public Lifetime Lifetime { get; }

    /// <summary>Initializes a new <see cref="ResolvedServiceType"/>.</summary>
    public ResolvedServiceType(string implementationType, string serviceType, Lifetime lifetime)
    {
        ImplementationType = implementationType;
        ServiceType = serviceType;
        Lifetime = lifetime;
    }
}
