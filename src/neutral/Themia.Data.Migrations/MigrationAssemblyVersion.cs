using System.Reflection;

namespace Themia.Data.Migrations;

/// <summary>
/// Refuses to drive a <b>Themia</b> migration assembly whose version does not match the runner's.
/// </summary>
/// <remarks>
/// coord #0085. A production image held <c>Themia.Data.Migrations</c> 0.16.0 driving
/// <c>Themia.AspNetCore.DataProtection</c> 0.15.0: the 0.16 runner started an empty per-assembly ledger,
/// so it replayed a 0.15 migration that had no adopt-if-exists guard, and the host crash-looped on
/// <c>CREATE TABLE</c>. Themia ships every package at one version precisely so that combination does not
/// arise, and nothing enforced it.
/// <para>
/// <b>How it arises without anyone doing anything wrong.</b> The reported consumer had no lock file. They
/// use central package management with <c>CentralPackageTransitivePinningEnabled</c>, reference only
/// <c>…DataProtection.PostgreSql</c> directly, and receive the core transitively. A grouped Dependabot PR
/// raised nine Themia entries and left that one behind, so transitive pinning held the core at what the
/// un-raised package asked for. <b>Nothing was downgraded</b> — a package was merely never raised — so
/// NuGet has nothing to warn about and the restore, build and tests are all green. The mismatch exists
/// only in the image, and is first observable as a boot crash.
/// </para>
/// <para>
/// The check compares MAJOR.MINOR only. Themia is a single-version monorepo, so in principle any
/// difference is unsupported; the minor is where a pre-1.0 breaking change lands (see CHANGELOG's
/// versioning policy), and refusing a patch difference would turn a harmless lag into a failed boot for
/// no stated reason.
/// </para>
/// <para>
/// <b>Only Themia assemblies are checked.</b> Consumers pass their own migration assemblies to
/// <see cref="ThemiaMigrations.Run(MigrationEngine, string, Assembly[])"/> — that is the supported way to
/// use it — and their versioning has nothing to do with ours. Checking them would break every consumer on
/// the first upgrade.
/// </para>
/// </remarks>
internal static class MigrationAssemblyVersion
{
    /// <summary>The prefix that marks an assembly as one of ours.</summary>
    private const string ThemiaPrefix = "Themia.";

    /// <summary>
    /// Throws when <paramref name="migrationAssembly"/> is a Themia assembly built from a different
    /// major.minor than the runner.
    /// </summary>
    /// <param name="migrationAssembly">The assembly about to be migrated.</param>
    /// <exception cref="InvalidOperationException">The versions differ.</exception>
    internal static void Verify(Assembly migrationAssembly)
    {
        var runner = typeof(ThemiaMigrations).Assembly;
        var message = Describe(
            Read(runner), runner.GetName().Name, Read(migrationAssembly), migrationAssembly.GetName().Name);

        if (message is not null)
        {
            throw new InvalidOperationException(message);
        }
    }

    /// <summary>
    /// The mismatch message, or <see langword="null"/> when the pair is acceptable. Split out from
    /// <see cref="Verify"/> so the rules are testable without building assemblies to order.
    /// </summary>
    /// <param name="runnerVersion">The runner's version.</param>
    /// <param name="runnerName">The runner's assembly name.</param>
    /// <param name="assemblyVersion">The migration assembly's version.</param>
    /// <param name="assemblyName">The migration assembly's name.</param>
    /// <returns>A message naming both sides, or <see langword="null"/>.</returns>
    internal static string? Describe(
        Version? runnerVersion, string? runnerName, Version? assemblyVersion, string? assemblyName)
    {
        // Not ours: a consumer's own migration assembly, which is the documented way to use the runner.
        if (assemblyName is null || !assemblyName.StartsWith(ThemiaPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        // Fail OPEN on an unreadable version. A missing attribute is not evidence of a mismatch, and this
        // guard must never be the reason a correctly-versioned deployment cannot boot.
        if (runnerVersion is null || assemblyVersion is null)
        {
            return null;
        }

        if (runnerVersion.Major == assemblyVersion.Major && runnerVersion.Minor == assemblyVersion.Minor)
        {
            return null;
        }

        return $"Themia.Data.Migrations: refusing to apply migrations from '{assemblyName}' "
            + $"{assemblyVersion} using the {runnerName} {runnerVersion} runner. Themia ships every "
            + "package at one version, and driving a migration set from a different one is how coord #0085 "
            + "reached production: the newer runner starts an empty per-assembly version ledger and replays "
            + "the older migrations, which may predate the guards that make a replay safe. "
            + "Align every Themia package to the same version. If they were bumped by a dependency bot, "
            + "check for one that was left behind — under central package management a package that is "
            + "never RAISED produces no NuGet warning at all.";
    }

    /// <summary>Reads an assembly's version, preferring the informational one.</summary>
    /// <param name="assembly">The assembly to read.</param>
    /// <returns>The parsed version, or <see langword="null"/> if none can be read.</returns>
    /// <remarks>
    /// The informational version is what the package version sets, so it is the one that answers "which
    /// release is this". It carries build metadata when SourceLink is enabled (<c>0.16.1+sha</c>), and a
    /// prerelease label would make it unparseable, so everything from the first <c>-</c> or <c>+</c> is
    /// dropped before parsing. The assembly version is the fallback.
    /// </remarks>
    internal static Version? Read(Assembly assembly)
    {
        var informational = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(informational))
        {
            var core = informational.AsSpan();
            var cut = core.IndexOfAny('-', '+');
            if (cut >= 0)
            {
                core = core[..cut];
            }

            if (Version.TryParse(core, out var parsed))
            {
                return parsed;
            }
        }

        return assembly.GetName().Version;
    }
}
