using HandlebarsDotNet;

namespace Themia.Notifications;

/// <summary>Handlebars.Net-backed <see cref="INotificationTemplateRenderer"/>. Thread-safe for rendering.
/// Uses Handlebars.Net directly (not via Themia.Pdf, which would pull in PuppeteerSharp/Chromium).</summary>
internal sealed class HandlebarsNotificationRenderer : INotificationTemplateRenderer
{
    private readonly IHandlebars _hbs;

    public HandlebarsNotificationRenderer(ThemiaNotificationsOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        _hbs = Handlebars.Create();
        options.ConfigureHandlebars?.Invoke(_hbs);
    }

    public string Render(string template, object model)
    {
        ArgumentNullException.ThrowIfNull(template);
        // ponytail: compiles per call. Add a bounded compiled-template cache keyed by `template` if profiling shows it matters.
        return _hbs.Compile(template)(model);
    }
}
