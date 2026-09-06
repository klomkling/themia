using System.Diagnostics;
using System.IO.Compression;
using System.Xml.Linq;

using Xunit;

namespace Themia.Imaging.Tests;

/// <summary>
/// Pins what <c>Themia.Imaging</c> declares as dependencies in its PACKED nuspec — not what its csproj
/// appears to reference, which is a different thing and was the whole defect.
/// </summary>
/// <remarks>
/// coord #0110: the shipped 0.21.4 nuspec carried <c>SkiaSharp.NativeAssets.macos</c> and no Linux
/// equivalent, even though the csproj referenced only managed SkiaSharp and the README said so in as
/// many words. SkiaSharp declares the macOS natives transitively for every modern target framework, and
/// this repo enables <c>CentralPackageTransitivePinningEnabled</c>, which promotes a pinned transitive
/// into a DIRECT dependency of the produced package.
/// <para>
/// The harm was not the extra megabytes. Supplying macOS natives made the package look self-contained on
/// a developer's Mac, so the README's instruction to add a native package went unread and unneeded —
/// until the Linux container, where the first decode threw.
/// </para>
/// <para>
/// Suppressing the promotion alone would not have fixed that: consumers still receive macOS and Win32
/// natives from SkiaSharp itself, so a Mac still looks self-contained. The package therefore also ships
/// the LINUX codec, which is the one platform SkiaSharp leaves opt-in and the one every deployment runs.
/// This test pins both halves — Linux present, macos and Win32 absent from OUR declaration.
/// </para>
/// <para>
/// Integration-tagged for the same reason as <c>MetaPackagePackTests</c>: it shells out to a real
/// <c>dotnet pack</c>, which deadlocks against the parent test host's build server if servers are left
/// enabled.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class ImagingPackTests
{
    /// <summary>
    /// Exactly what a consumer takes. Managed SkiaSharp plus the LINUX native codec — deliberately, and
    /// deliberately only Linux: SkiaSharp already forces macOS and Win32 natives on every consumer
    /// transitively, so Linux is the only platform a consumer would otherwise have to supply, and the
    /// only one every Idevs deployment actually runs.
    /// </summary>
    private static readonly string[] ExpectedDependencyIds =
    [
        "Microsoft.Extensions.DependencyInjection.Abstractions",
        "Microsoft.Extensions.Options",
        "SkiaSharp",
        "SkiaSharp.NativeAssets.Linux",
    ];

    [Fact]
    public void Pack_DeclaresSkiaSharpAndLinuxNatives_AndNoOtherRidsNatives()
    {
        var repoRoot = FindRepoRoot();
        var project = Path.Combine(repoRoot, "src", "neutral", "Themia.Imaging", "Themia.Imaging.csproj");
        var outDir = Path.Combine(Path.GetTempPath(), $"themia-imagingpack-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outDir);
        try
        {
            RunDotnet($"pack \"{project}\" --output \"{outDir}\" --disable-build-servers", repoRoot);

            var nupkg = Assert.Single(Directory.GetFiles(outDir, "*.nupkg"));
            using var zip = ZipFile.OpenRead(nupkg);

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

            // Asserted separately from the exact list so a failure names the actual disease. macos
            // reaching the nuspec means transitive pinning has promoted it again — the coord #0110 defect
            // — which reads as a generic "list changed" and invites someone to update the expected list
            // rather than ask why a RID we do not target is being declared.
            Assert.DoesNotContain(dependencyIds, id =>
                id.Contains("macos", StringComparison.OrdinalIgnoreCase)
                || id.Contains("Win32", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            // Best-effort cleanup: a delete failure must not mask a real assertion failure above.
            try { Directory.Delete(outDir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
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
        psi.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start dotnet.");
        // Drain both pipes concurrently before waiting: dotnet pack is chatty enough to fill a buffer.
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();
        Assert.True(process.ExitCode == 0, $"dotnet {arguments} failed ({process.ExitCode}):\n{stdout}\n{stderr}");
    }
}
