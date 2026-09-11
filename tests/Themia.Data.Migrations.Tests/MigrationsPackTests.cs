using System.Diagnostics;
using System.IO.Compression;
using System.Xml.Linq;

using Xunit;

namespace Themia.Data.Migrations.Tests;

/// <summary>
/// Pins the PACKED nuspec dependency set of <c>Themia.Data.Migrations</c> and of the packages that must
/// not reintroduce a driver behind it — not their csprojs' <c>PackageReference</c> lists, which are a
/// different thing and are exactly how the defect this test guards against arrived.
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

    /// <summary>
    /// The packages that declared a driver THEMSELVES, so the engine split never touched them: they were
    /// not on the transitive path it fixed. <c>Themia.Exceptional</c> carried
    /// <c>Microsoft.Data.SqlClient</c> (with a <c>VersionOverride</c>) while naming no provider type in
    /// any of its own <c>.cs</c> files, and <c>Themia.Scheduling</c> carried <c>Npgsql</c> AND
    /// <c>Microsoft.Data.SqlClient</c> to feed a <c>switch</c> that constructed both. ezy-assets' image
    /// therefore still carried <c>Microsoft.IdentityModel.*</c> and <c>Azure.Identity</c> after 0.25.0 —
    /// through these two rather than through Audit (coord #0126). Nothing pinned them, which is why the
    /// class came back at all; it is pinned now.
    /// </summary>
    public static TheoryData<string, string> DriverFreePackages() => new()
    {
        { "Themia.Exceptional", "net8.0;net10.0" },
        { "Themia.Scheduling", "net10.0" },
    };

    [Theory]
    [MemberData(nameof(DriverFreePackages))]
    public void Pack_CarriesNoAdoDriver(string packageId, string targetFrameworks)
    {
        // targetFrameworks is documentation carried into the failure message: which legs SHOULD appear,
        // so a group silently disappearing from the nuspec is legible rather than a vacuous pass.
        var repoRoot = FindRepoRoot();
        var project = Path.Combine(repoRoot, "src", "neutral", packageId, $"{packageId}.csproj");
        var outDir = Path.Combine(Path.GetTempPath(), $"themia-driverpack-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outDir);
        try
        {
            RunDotnet($"pack \"{project}\" --output \"{outDir}\" --disable-build-servers", repoRoot);

            var nupkg = Directory.GetFiles(outDir, $"{packageId}.*.nupkg")
                .Single(f => !f.EndsWith(".snupkg", StringComparison.Ordinal));
            using var zip = ZipFile.OpenRead(nupkg);

            var nuspecEntry = zip.Entries.Single(e => e.FullName.EndsWith(".nuspec", StringComparison.Ordinal));
            using var nuspecStream = nuspecEntry.Open();
            var nuspec = XDocument.Load(nuspecStream);

            // Scoped to <dependencies> groups specifically, unlike the test above: these packages also
            // carry <frameworkReferences> groups (Microsoft.AspNetCore.App), and those are groups too.
            var groups = nuspec.Descendants()
                .Where(e => e.Name.LocalName == "dependencies")
                .SelectMany(d => d.Elements().Where(e => e.Name.LocalName == "group"))
                .ToArray();
            Assert.True(groups.Length > 0, $"{packageId} ({targetFrameworks}) packed no dependency group.");

            foreach (var group in groups)
            {
                var targetFramework = (string?)group.Attribute("targetFramework") ?? "(unlabelled)";

                var dependencyIds = group.Descendants()
                    .Where(e => e.Name.LocalName == "dependency")
                    .Select(e => (string?)e.Attribute("id"))
                    .Where(id => id is not null)
                    .Select(id => id!)
                    .ToArray();

                foreach (var forbidden in ForbiddenDependencyIds)
                {
                    Assert.False(
                        dependencyIds.Any(id => id.Equals(forbidden, StringComparison.OrdinalIgnoreCase)),
                        $"{packageId} [{targetFramework}] declares the ADO driver '{forbidden}'. "
                        + $"Full dependency set: [{string.Join(", ", dependencyIds.OrderBy(id => id, StringComparer.Ordinal))}]");
                }
            }
        }
        finally
        {
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

    /// <summary>
    /// Runs one <c>dotnet pack</c>, serialized against any other test host packing the same project.
    /// </summary>
    /// <remarks>
    /// This assembly is multi-targeted, so xUnit runs it as TWO test hosts (net8 and net10) concurrently
    /// and both execute every pack test. Both then pack the SAME project into the SAME
    /// <c>bin/Release/&lt;tfm&gt;</c> — most visibly for <c>Themia.Scheduling</c>, which is net10.0-only,
    /// so the two hosts collide on one output directory rather than on two. Observed from a cold
    /// <c>obj</c>: <c>GenerateDepsFile</c> failing with <c>IOException: The process cannot access the
    /// file 'Themia.Scheduling.deps.json' because it is being used by another process</c>.
    /// <para>
    /// A named mutex, not per-invocation <c>BaseOutputPath</c>/<c>BaseIntermediateOutputPath</c>: those
    /// are global MSBuild properties that apply to every REFERENCED project too, so all of them restore
    /// into one shared <c>obj</c> and the target project's own assets file is overwritten —
    /// <c>NETSDK1005: Assets file … doesn't have a target for 'net8.0'</c>, measured. Serializing keeps
    /// the ordinary build layout and removes the concurrency instead.
    /// </para>
    /// </remarks>
    private static void RunDotnet(string arguments, string workingDirectory)
    {
        // Named per repo root: every pack test in this assembly shares the repo's bin/obj tree, so one
        // gate for all of them is both sufficient and the simplest thing that cannot deadlock.
        using var gate = new Mutex(initiallyOwned: false, MutexName(workingDirectory));
        var held = false;
        try
        {
            // AbandonedMutexException means the other host died holding it; the lock is ours either way.
            try { held = gate.WaitOne(TimeSpan.FromMinutes(10)); }
            catch (AbandonedMutexException) { held = true; }

            Assert.True(held, "Timed out waiting for the pack lock held by the other test host.");
            RunDotnetCore(arguments, workingDirectory);
        }
        finally
        {
            if (held)
            {
                gate.ReleaseMutex();
            }
        }
    }

    /// <summary>A filesystem-safe, collision-free mutex name derived from the repo root path.</summary>
    private static string MutexName(string workingDirectory) =>
        "themia-pack-" + Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes(workingDirectory)))[..16];

    private static void RunDotnetCore(string arguments, string workingDirectory)
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
