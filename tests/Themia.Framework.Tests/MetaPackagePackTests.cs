using System.Diagnostics;
using System.IO.Compression;
using System.Xml.Linq;
using Xunit;

namespace Themia.Framework.Tests;

public sealed class MetaPackagePackTests
{
    private static readonly string[] ExpectedDependencyIds =
    [
        "Themia.Caching",
        "Themia.Framework.AspNetCore",
        "Themia.Framework.Core",
        "Themia.Framework.Data.Abstractions",
        "Themia.Logging",
        "Themia.Mediator",
        "Themia.MultiTenancy",
        "Themia.MultiTenancy.Mediator",
        "Themia.Services",
    ];

    [Fact]
    public void Pack_ProducesDependencyOnlyNupkg_WithExactExpectedDependencies()
    {
        var repoRoot = FindRepoRoot();
        var project = Path.Combine(repoRoot, "src", "framework", "Themia.Framework", "Themia.Framework.csproj");
        var outDir = Path.Combine(Path.GetTempPath(), $"themia-metapack-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outDir);
        try
        {
            RunDotnet($"pack \"{project}\" --output \"{outDir}\"", repoRoot);

            // Assembly-less: exactly one .nupkg, and no symbols package (IncludeSymbols
            // is overridden to false — there is no assembly to produce symbols for).
            var nupkg = Assert.Single(Directory.GetFiles(outDir, "*.nupkg"));
            Assert.Empty(Directory.GetFiles(outDir, "*.snupkg"));

            using var zip = ZipFile.OpenRead(nupkg);
            Assert.DoesNotContain(zip.Entries, e => e.FullName.StartsWith("lib/", StringComparison.Ordinal));

            var nuspecEntry = zip.Entries.Single(e => e.FullName.EndsWith(".nuspec", StringComparison.Ordinal));
            using var nuspecStream = nuspecEntry.Open();
            var nuspec = XDocument.Load(nuspecStream);

            // Local-name matching sidesteps the nuspec XML namespace.
            var dependencyIds = nuspec.Descendants()
                .Where(e => e.Name.LocalName == "dependency")
                .Select(e => (string?)e.Attribute("id"))
                .Where(id => id is not null)
                .Select(id => id!)
                .Distinct()
                .OrderBy(id => id, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(ExpectedDependencyIds, dependencyIds);
        }
        finally
        {
            Directory.Delete(outDir, recursive: true);
        }
    }

    private static string FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Themia.sln")))
        {
            dir = dir.Parent;
        }

        return dir?.FullName
            ?? throw new InvalidOperationException("Could not locate Themia.sln above the test base directory.");
    }

    private static void RunDotnet(string arguments, string workingDirectory)
    {
        var psi = new ProcessStartInfo("dotnet", arguments)
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start dotnet.");
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"dotnet {arguments} failed ({process.ExitCode}):\n{stdout}\n{stderr}");
    }
}
