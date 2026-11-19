using Microsoft.Extensions.FileProviders;
using Themia.Quartz.Dashboard;
using Xunit;

namespace Themia.Quartz.Tests;

/// <summary>
/// Every shipped view that declares a layout must have the directive stripped (the view engine applies the layout
/// itself), and an unrecognised directive must fail closed rather than reach Handlebars.Net, which would render the
/// view with output encoding suppressed.
/// </summary>
public sealed class ViewFileSystemFactoryTests
{
    private const string ViewsNamespace = "Themia.Quartz.Dashboard.Views";
    private const string DirectiveMarker = "{{!<";

    private static IEnumerable<string> ShippedViews(IFileProvider provider, string directory = "")
    {
        foreach (var entry in provider.GetDirectoryContents(directory))
        {
            var path = string.IsNullOrEmpty(directory) ? entry.Name : $"{directory}/{entry.Name}";
            if (entry.IsDirectory)
            {
                foreach (var nested in ShippedViews(provider, path))
                {
                    yield return nested;
                }
            }
            else if (path.EndsWith(".hbs", StringComparison.Ordinal))
            {
                yield return path;
            }
        }
    }

    [Fact]
    public void GetFileContent_ShouldNeverReturnALayoutDirective_ForAnyShippedView()
    {
        var provider = new EmbeddedFileProvider(typeof(ViewFileSystemFactory).Assembly, ViewsNamespace);
        var views = ShippedViews(provider).ToList();
        var fs = ViewFileSystemFactory.Create(new ThemiaQuartzOptions());

        Assert.NotEmpty(views);
        Assert.All(views, view =>
            Assert.DoesNotContain(DirectiveMarker, fs.GetFileContent(view), StringComparison.Ordinal));
    }
}
