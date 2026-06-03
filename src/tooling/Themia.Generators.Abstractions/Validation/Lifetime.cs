namespace Themia.Generators.Abstractions.Validation;

/// <summary>
/// Represents the DI lifetime for a registered service.
/// </summary>
public enum Lifetime
{
    /// <summary>Scoped lifetime: one instance per scope (e.g. per HTTP request).</summary>
    Scoped = 0,

    /// <summary>Singleton lifetime: one instance for the application lifetime.</summary>
    Singleton = 1,

    /// <summary>Transient lifetime: a new instance each time it is requested.</summary>
    Transient = 2,
}
