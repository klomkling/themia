namespace Themia.Framework.Core.Modules;

/// <summary>
/// Describes a module including identity and dependencies.
/// </summary>
public sealed class ModuleDescriptor
{
    /// <summary>
    /// Initializes a new module descriptor.
    /// </summary>
    /// <param name="name">Unique module name.</param>
    /// <param name="displayName">Optional display name.</param>
    /// <param name="description">Optional description.</param>
    /// <param name="version">Module version.</param>
    /// <param name="dependencies">Names of modules this module depends on.</param>
    public ModuleDescriptor(string name, string? displayName = null, string? description = null, Version? version = null, IReadOnlyCollection<string>? dependencies = null)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("Module name cannot be null or whitespace.", nameof(name));
        }

        Name = name;
        DisplayName = displayName ?? name;
        Description = description;
        Version = version ?? new Version(1, 0, 0, 0);
        Dependencies = dependencies ?? Array.Empty<string>();
    }

    /// <summary>
    /// Gets the unique module name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the human-friendly display name.
    /// </summary>
    public string DisplayName { get; }

    /// <summary>
    /// Gets an optional description of the module.
    /// </summary>
    public string? Description { get; }

    /// <summary>
    /// Gets the module semantic version.
    /// </summary>
    public Version Version { get; }

    /// <summary>
    /// Gets dependent module names.
    /// </summary>
    public IReadOnlyCollection<string> Dependencies { get; }
}
