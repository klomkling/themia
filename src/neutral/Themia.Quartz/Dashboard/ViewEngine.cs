using HandlebarsDotNet;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;

namespace Themia.Quartz.Dashboard
{
    public class ViewEngine
    {
        readonly Services _services;
        readonly Dictionary<string, HandlebarsTemplate<object, string>> _compiledViews = new Dictionary<string, HandlebarsTemplate<object, string>>(StringComparer.OrdinalIgnoreCase);

        public bool UseCache { get; set; }

        public ViewEngine(Services services)
        {
            _services = services;
            UseCache = string.IsNullOrEmpty(services.Options.ViewsRootDirectory);
        }

        HandlebarsTemplate<object, string> GetRenderDelegate(string templatePath)
        {
            if (UseCache)
            {
                lock (_compiledViews)
                {
                    if (!_compiledViews.ContainsKey(templatePath))
                    {
                        _compiledViews[templatePath] = _services.Handlebars.CompileView(templatePath);
                    }

                    return _compiledViews[templatePath];
                }
            }
            else
            {
                return _services.Handlebars.CompileView(templatePath);
            }
        }

        const string LayoutName = "Layout";
        readonly Dictionary<string, bool> _usesLayout = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

        bool UsesLayout(string templatePath)
        {
            var fileSystem = (ViewFileSystemFactory.LayoutAwareFileSystem)_services.Handlebars.Configuration.FileSystem;
            if (!UseCache)
            {
                return fileSystem.UsesLayout(templatePath);
            }

            lock (_usesLayout)
            {
                if (!_usesLayout.TryGetValue(templatePath, out var uses))
                {
                    _usesLayout[templatePath] = uses = fileSystem.UsesLayout(templatePath);
                }

                return uses;
            }
        }

        // The view is rendered on its own (see ViewFileSystemFactory.LayoutAwareFileSystem), then handed to
        // the layout as Page.Body.
        public string Render(string templatePath, object model)
        {
            if (!UsesLayout(templatePath))
            {
                return GetRenderDelegate(templatePath)(model);
            }

            if (model is not Controllers.PageControllerBase.Page page)
            {
                throw new InvalidOperationException(
                    $"View '{templatePath}' declares a layout, so it must be rendered with a PageControllerBase.Page model (got {model?.GetType().FullName ?? "null"}).");
            }

            var body = GetRenderDelegate(templatePath)(model);
            page.Body = body;
            return GetRenderDelegate(LayoutName + ".hbs")(page);
        }

        public string Encode(object value)
        {
            return WebUtility.HtmlEncode(string.Format(CultureInfo.InvariantCulture, "{0}", value));
        }

        public string ErrorPage(Exception ex)
        {
            return Render("Error.hbs", new
            {
                ex.GetBaseException().GetType().FullName,
                Exception = ex,
                BaseException = ex.GetBaseException(),
                Dump = ex.ToString()
            });
        }
    }
}
