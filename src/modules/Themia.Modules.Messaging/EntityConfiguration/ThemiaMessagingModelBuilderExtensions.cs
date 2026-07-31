using Microsoft.EntityFrameworkCore;

namespace Themia.Modules.Messaging.EntityConfiguration;

/// <summary>Applies the Messaging module's EF Core entity configurations.</summary>
public static class ThemiaMessagingModelBuilderExtensions
{
    /// <summary>
    /// Registers the outbox entity on the given model. Call from your
    /// <c>ThemiaDbContext.OnModelCreating</c>; the base context applies tenant and soft-delete
    /// query filters to entities implementing the framework marker interfaces.
    /// </summary>
    /// <param name="modelBuilder">The model builder to configure.</param>
    /// <returns>The same model builder, for chaining.</returns>
    public static ModelBuilder ApplyThemiaMessaging(this ModelBuilder modelBuilder)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        modelBuilder.ApplyConfiguration(new MessageOutboxEntryConfiguration());
        return modelBuilder;
    }
}
