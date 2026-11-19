using HandlebarsDotNet;
using Microsoft.Extensions.FileProviders;
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;

namespace Themia.Quartz.Dashboard
{
    public static class ViewFileSystemFactory
    {
        public static ViewEngineFileSystem Create(ThemiaQuartzOptions options)
        {
            LayoutAwareFileSystem fs;
            if (string.IsNullOrEmpty(options.ViewsRootDirectory))
            {
                fs = new EmbeddedFileSystem();
            }
            else
            {
                fs = new DiskFileSystem(options.ViewsRootDirectory);
            }

            return fs;
        }

        // Handlebars.Net 2.1.6 renders a view that declares a layout ({{!<Layout}}) with output encoding
        // suppressed, so every {{value}} in it reached the page raw (stored XSS through job / trigger /
        // calendar names). Views are therefore served without the directive and ViewEngine applies the
        // layout itself, so {{value}} stays HTML-encoded.
        internal abstract class LayoutAwareFileSystem : ViewEngineFileSystem
        {
            const string LayoutName = "Layout";
            static readonly Regex Directive = new Regex(@"^\uFEFF?\s*\{\{!<\s*(\w+)\s*\}\}", RegexOptions.Compiled);
            const string DirectiveMarker = "{{!<";

            protected abstract string ReadRaw(string filename);

            public override string GetFileContent(string filename)
            {
                var raw = ReadRaw(filename);
                var directive = Match(raw, filename);
                return directive.Success ? raw.Substring(directive.Length) : raw;
            }

            public bool UsesLayout(string filename)
            {
                var directive = Match(ReadRaw(filename), filename);
                if (directive.Success && directive.Groups[1].Value != LayoutName)
                {
                    throw new InvalidOperationException($"Unsupported layout '{directive.Groups[1].Value}' in '{filename}'.");
                }

                return directive.Success;
            }

            // Fail closed: a layout directive the regex does not recognise would leave Handlebars.Net to
            // render the view with output encoding suppressed, so it is rejected instead of served.
            static Match Match(string raw, string filename)
            {
                var directive = Directive.Match(raw);
                if (!directive.Success && raw.Contains(DirectiveMarker, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException($"Unrecognised layout directive in '{filename}'; it must be the first token of the view.");
                }

                return directive;
            }
        }

        private class DiskFileSystem : LayoutAwareFileSystem
        {
            string root;

            public DiskFileSystem(string root)
            {
                this.root = root;
            }

            protected override string ReadRaw(string filename)
            {
                return File.ReadAllText(GetFullPath(filename));
            }

            protected override string CombinePath(string dir, string otherFileName)
            {
                return Path.Combine(dir, otherFileName);
            }

            public override bool FileExists(string filePath)
            {
                return File.Exists(GetFullPath(filePath));
            }

            string GetFullPath(string filePath)
            {
                return Path.Combine(root, filePath.Replace("partials/", "Partials/").Replace('/', Path.DirectorySeparatorChar));
            }
        }

        private class EmbeddedFileSystem : LayoutAwareFileSystem
        {
            private EmbeddedFileProvider fs = new EmbeddedFileProvider(Assembly.GetExecutingAssembly(), "Themia.Quartz.Dashboard.Views");
            protected override string ReadRaw(string filename)
            {
                var result = string.Empty;
                var fi = fs.GetFileInfo(GetFullPath(filename));
                using (var stream =fi.CreateReadStream())
                {
                    using (var reader = new StreamReader(stream))
                    {
                        result = reader.ReadToEnd();
                    }
                }
                return result;
            }

            protected override string CombinePath(string dir, string otherFileName)
            {
                return Path.Combine(dir, otherFileName);
            }

            public override bool FileExists(string filePath)
            {
                return fs.GetFileInfo(GetFullPath(filePath)).Exists;
            }
            string GetFullPath(string filePath)
            {
                return filePath.Replace("partials/", "Partials/").Replace('/', Path.DirectorySeparatorChar);
            }

        }

    }
}
