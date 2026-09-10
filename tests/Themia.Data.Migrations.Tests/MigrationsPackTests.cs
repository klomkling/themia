using System.Diagnostics;
using System.IO.Compression;
using System.Xml.Linq;

using Xunit;

namespace Themia.Data.Migrations.Tests;

/// <summary>
/// Pins <c>Themia.Data.Migrations</c>'s PACKED nuspec dependency set — not its csproj's
/// <c>PackageReference</c> list, which is a different thing and is exactly how the defect this test
/// guards against arrived.
/// </summary>
/// <remarks>
/// coord #0116, #0117 (spec §1: <c>docs/superpowers/specs/2026-09-09-data-migrations-engine-split.md</c>):
/// the pre-split core measured at fifteen nuspec dependencies because <c>Themia.Audit</c> held a
/// <c>ProjectReference</c> to it that flattened three FluentMigrator runners and three ADO drivers into
/// every consumer's restore graph. This test is the regression guard the spec calls for in §9/§10 — it
/// must fail the moment <c>Npgsql</c>, <c>MySqlConnector</c> or <c>Microsoft.Data.SqlClient</c> return to
/// the core by any path, a bare <c>PackageReference</c> or a convenient <c>ProjectReference</c> alike.
/// <para>
/// The expected list is measured from the packed nuspec, not copied from the csproj. This repo enables
/// <c>CentralPackageTransitivePinningEnabled</c> (the same mechanism <c>ImagingPackTests</c> pins for
/// coord #0110), which promotes every CPM-pinned transitive package into a direct nuspec dependency. The
/// core's csproj lists two <c>Microsoft.Extensions.*</c> <c>PackageReference</c>s
/// (<c>Microsoft.Extensions.DependencyInjection</c>, <c>Microsoft.Extensions.Logging.Abstractions</c>),
/// but <c>FluentMigrator.Runner.Core</c> and <c>Microsoft.Extensions.DependencyInjection</c> each pin
/// further <c>Microsoft.Extensions.*</c> packages, and CPM promotes all of them into the nuspec — six in
/// total. A consumer's restore graph sees the six, not the two, so the six is what this test pins.
/// </para>
/// <para>
/// Integration-tagged for the same reason as <c>MetaPackagePackTests</c>/<c>ImagingPackTests</c>: it
/// shells out to a real <c>dotnet pack</c>, which deadlocks against the parent test host's build server
/// if servers are left enabled.
/// </para>
/// </remarks>
[Trait("Category", "Integration")]
public sealed class MigrationsPackTests
{
    /// <summary>
    /// Exactly what a consumer takes: the FluentMigrator core (runner-agnostic) plus the
    /// <c>Microsoft.Extensions.*</c> packages CPM promotes from it — and, deliberately, no engine runner
    /// and no ADO driver for any of the three engines.
    /// </summary>
    private static readonly string[] ExpectedDependencyIds =
    [
        "FluentMigrator",
        "FluentMigrator.Runner.Core",
        "Microsoft.Extensions.Configuration.Abstractions",
        "Microsoft.Extensions.DependencyInjection",
        "Microsoft.Extensions.DependencyInjection.Abstractions",
        "Microsoft.Extensions.Logging",
        "Microsoft.Extensions.Logging.Abstractions",
        "Microsoft.Extensions.Options",
    ];

    /// <summary>
    /// The three engine-specific ADO drivers this split moved out of the core. Asserted separately from
    /// <see cref="ExpectedDependencyIds"/> so a failure names the actual disease — any of these
    /// returning is the exact coord #0116/#0117 defect, not a generic "the dependency list changed".
    /// </summary>
    private static readonly string[] ForbiddenDependencyIds =
    [
        "Npgsql",
        "MySqlConnector",
        "Microsoft.Data.SqlClient",
    ];

    [Fact]
    public void Pack_ResolvesToTheEngineNeutralDependencySet_WithNoAdoDriver()
    {
        var repoRoot = FindRepoRoot();
        var project = Path.Combine(repoRoot, "src", "neutral", "Themia.Data.Migrations", "Themia.Data.Migrations.csproj");
        var outDir = Path.Combine(Path.GetTempPath(), $"themia-migrationspack-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outDir);
        try
        {
            RunDotnet($"pack \"{project}\" --output \"{outDir}\" --disable-build-servers", repoRoot);

            var nupkg = Assert.Single(Directory.GetFiles(outDir, "*.nupkg"));
            using var zip = ZipFile.OpenRead(nupkg);

            var nuspecEntry = zip.Entries.Single(e => e.FullName.EndsWith(".nuspec", StringComparison.Ordinal));
            using var nuspecStream = nuspecEntry.Open();
            var nuspec = XDocument.Load(nuspecStream);

            // net8.0;net10.0 (neutral cross-framework package, per CLAUDE.md's target-framework policy),
            // so the nuspec carries one <group> per TFM. Assert every group, not just the first: a
            // driver could in principle leak back into only one leg.
            var groups = nuspec.Descendants().Where(e => e.Name.LocalName == "group").ToArray();
            Assert.NotEmpty(groups);

            foreach (var group in groups)
            {
                var targetFramework = (string?)group.Attribute("targetFramework") ?? "(unlabelled)";

                // Local-name matching sidesteps the nuspec XML namespace.
                var dependencyIds = group.Descendants()
                    .Where(e => e.Name.LocalName == "dependency")
                    .Select(e => (string?)e.Attribute("id"))
                    .Where(id => id is not null)
                    .Select(id => id!)
                    .Distinct()
                    .OrderBy(id => id, StringComparer.Ordinal)
                    .ToArray();

                Assert.True(
                    ExpectedDependencyIds.SequenceEqual(dependencyIds),
                    $"[{targetFramework}] expected [{string.Join(", ", ExpectedDependencyIds)}] " +
                    $"but got [{string.Join(", ", dependencyIds)}]");

                foreach (var forbidden in ForbiddenDependencyIds)
                {
                    Assert.DoesNotContain(
                        dependencyIds,
                        id => id.Equals(forbidden, StringComparison.OrdinalIgnoreCase));
                }
            }
        }
        finally
        {
            // Best-effort cleanup: a delete failure (file lock, AV scanner, partial pack output) must
            // not mask a real pack assertion failure above.
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
        // Belt-and-suspenders with --disable-build-servers: never reuse or start an MSBuild node, so a
        // nested dotnet invocation cannot deadlock against a build server the test host is using.
        psi.Environment["DOTNET_CLI_USE_MSBUILD_SERVER"] = "0";
        psi.Environment["MSBUILDDISABLENODEREUSE"] = "1";
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start dotnet.");
        // Drain both pipes concurrently before waiting: reading one stream to the end while the child
        // fills the other's buffer would deadlock (dotnet pack is chatty).
        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        process.WaitForExit();
        var stdout = stdoutTask.GetAwaiter().GetResult();
        var stderr = stderrTask.GetAwaiter().GetResult();
        Assert.True(process.ExitCode == 0, $"dotnet {arguments} failed ({process.ExitCode}):\n{stdout}\n{stderr}");
    }
}
