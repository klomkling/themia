using HandlebarsDotNet;

namespace Themia.Notifications;

/// <summary>Process-wide configuration for the notification core.</summary>
public sealed class ThemiaNotificationsOptions
{
    /// <summary>Hook to register custom Handlebars helpers/partials at construction. Default <see langword="null"/>.</summary>
    public Action<IHandlebars>? ConfigureHandlebars { get; set; }
}
