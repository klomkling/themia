using HandlebarsDotNet;
using Microsoft.Extensions.DependencyInjection;
using Themia.Pdf;
using Xunit;

namespace Themia.ETax.Tests;

public sealed class RenderingRegistrationTests
{
    [Fact]
    public void AddThemiaETaxRendering_ShouldRegisterTheRendererOnTopOfThemiaPdf()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddThemiaETaxRendering();
        using var sp = services.BuildServiceProvider();

        Assert.Same(sp.GetRequiredService<TaxDocumentRenderer>(), sp.GetRequiredService<TaxDocumentRenderer>());
        Assert.NotNull(sp.GetService<IVersionedPdfRenderer>());
    }

    [Fact]
    public void AddThemiaETaxRendering_ShouldBeIdempotent()
    {
        var services = new ServiceCollection();
        services.AddThemiaETaxRendering();
        services.AddThemiaETaxRendering();

        Assert.Single(services, d => d.ServiceType == typeof(TaxDocumentRenderer));
    }

    [Fact]
    public void RenderHtml_ShouldIgnoreTheAdoptersHandlebarsConfiguration()
    {
        var plain = ServicesWith(null);
        var customised = ServicesWith(hbs =>
        {
            hbs.Configuration.NoEscape = true;
            hbs.RegisterHelper("if", (output, context, arguments) => output.WriteSafeString("HIJACKED"));
        });
        var snapshot = Samples.Receipt() with { Buyer = new TaxParty { Name = "<b>Buyer</b> & Co", TaxId = Samples.BuyerTaxId } };

        // The hook really is active in the adopter's own renderer...
        Assert.Equal("<b>x</b>", customised.GetRequiredService<IHtmlTemplateRenderer>().Render("{{v}}", new { v = "<b>x</b>" }));

        var expected = plain.GetRequiredService<TaxDocumentRenderer>().RenderHtml(snapshot, TaxDocumentTemplates.CurrentVersion, TaxDocumentMarker.Original);
        var actual = customised.GetRequiredService<TaxDocumentRenderer>().RenderHtml(snapshot, TaxDocumentTemplates.CurrentVersion, TaxDocumentMarker.Original);

        // ...and still changes nothing in ETax's HTML.
        Assert.Equal(expected, actual);
        Assert.Contains("&lt;b&gt;Buyer&lt;/b&gt;", actual);
        Assert.DoesNotContain("HIJACKED", actual);
    }

    private static ServiceProvider ServicesWith(Action<HandlebarsDotNet.IHandlebars>? configure)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddThemiaETaxRendering(o => o.ConfigureHandlebars = configure);
        return services.BuildServiceProvider();
    }
}
