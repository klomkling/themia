using Serilog.Context;

namespace Themia.Logging;

/// <summary>
/// Provides methods for adding contextual properties to log events.
/// This is a wrapper around Serilog's LogContext to avoid direct Serilog dependencies in other Themia packages.
/// </summary>
public static class ThemiaLogContext
{
    /// <summary>
    /// Pushes a property onto the log context, which will be included in all log events within the scope.
    /// </summary>
    /// <param name="name">The property name.</param>
    /// <param name="value">The property value.</param>
    /// <param name="destructureObjects">If true, complex objects will be destructured; otherwise, they'll be ToString()'d.</param>
    /// <returns>A disposable that removes the property from the context when disposed.</returns>
    public static IDisposable PushProperty(string name, object? value, bool destructureObjects = false)
    {
        return LogContext.PushProperty(name, value, destructureObjects);
    }
}
