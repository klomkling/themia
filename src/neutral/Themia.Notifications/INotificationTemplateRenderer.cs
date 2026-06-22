namespace Themia.Notifications;

/// <summary>Merges a Handlebars template with a model into a notification body.</summary>
public interface INotificationTemplateRenderer
{
    /// <summary>Compiles <paramref name="template"/> as Handlebars and renders it against <paramref name="model"/>.</summary>
    string Render(string template, object model);
}
