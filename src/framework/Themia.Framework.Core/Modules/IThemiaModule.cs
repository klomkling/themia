using Microsoft.Extensions.DependencyInjection;

namespace Themia.Framework.Core.Modules;

/// <summary>
/// Contract for Themia modules to register services and perform startup initialization.
/// </summary>
public interface IThemiaModule
{
    /// <summary>
    /// Gets the module descriptor.
    /// </summary>
    ModuleDescriptor Descriptor { get; }

    /// <summary>
    /// Registers services required by the module.
    /// </summary>
    /// <param name="services">Service collection.</param>
    void ConfigureServices(IServiceCollection services);

    /// <summary>
    /// Performs asynchronous initialization when the application starts.
    /// </summary>
    /// <param name="serviceProvider">Application service provider.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>A task representing initialization.</returns>
    ValueTask InitializeAsync(IServiceProvider serviceProvider, CancellationToken cancellationToken = default);
}

/// <summary>
/// Base implementation of <see cref="IThemiaModule"/> with no-op defaults.
/// </summary>
public abstract class ThemiaModuleBase : IThemiaModule
{
    /// <inheritdoc />
    public abstract ModuleDescriptor Descriptor { get; }

    /// <inheritdoc />
    public virtual void ConfigureServices(IServiceCollection services)
    {
    }

    /// <inheritdoc />
    public virtual ValueTask InitializeAsync(IServiceProvider serviceProvider, CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;
}
