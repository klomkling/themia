using HandlebarsDotNet;

namespace Themia.Pdf;

/// <summary>Handlebars.Net-backed <see cref="IHtmlTemplateRenderer"/>. Thread-safe for rendering.</summary>
internal sealed class HandlebarsHtmlTemplateRenderer : IHtmlTemplateRenderer
{
    private readonly IHandlebars _hbs;

    public HandlebarsHtmlTemplateRenderer(ThemiaPdfOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        _hbs = Handlebars.Create();
        _hbs.RegisterHelper("emptyIfNull", (output, _, arguments) =>
            output.WriteSafeString(arguments.Length > 0 ? arguments[0]?.ToString() ?? string.Empty : string.Empty));

        options.ConfigureHandlebars?.Invoke(_hbs);
    }

    public string Render(string template, object model)
    {
        ArgumentNullException.ThrowIfNull(template);

        // ponytail: compiles per call (port-faithful with ezy). Add a bounded compiled-template
        // cache keyed by `template` if profiling shows compile cost matters.
        var compiled = _hbs.Compile(template);
        return compiled(model);
    }
}
