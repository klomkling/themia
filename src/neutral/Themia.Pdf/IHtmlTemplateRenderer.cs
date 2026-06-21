namespace Themia.Pdf;

/// <summary>Merges an HTML template with a data model and returns the resulting HTML string.</summary>
public interface IHtmlTemplateRenderer
{
    /// <summary>
    /// Compiles <paramref name="template"/> as a Handlebars template and renders it against
    /// <paramref name="model"/>.
    /// </summary>
    /// <param name="template">The Handlebars HTML template body.</param>
    /// <param name="model">The data model the template is rendered against.</param>
    /// <returns>The rendered HTML.</returns>
    string Render(string template, object model);
}
