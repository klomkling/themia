using Xunit;

namespace Themia.Data.Migrations.Tests;

/// <summary>
/// The guard that would have turned coord #0085 into a startup error instead of an outage.
/// </summary>
/// <remarks>
/// The rules are tested through <see cref="MigrationAssemblyVersion.Describe"/> rather than by building
/// assemblies at odd versions: the interesting behaviour is entirely in which pairs are refused, and an
/// assembly-generating fixture would test the fixture.
/// </remarks>
public class MigrationAssemblyVersionTests
{
    private static readonly Version Runner = new(0, 16, 1);

    [Fact]
    public void Refuses_a_themia_assembly_from_a_different_minor()
    {
        // The reported image, exactly: a 0.16 runner over a 0.15 migration set.
        var message = MigrationAssemblyVersion.Describe(
            Runner, "Themia.Data.Migrations", new Version(0, 15, 0), "Themia.AspNetCore.DataProtection");

        Assert.NotNull(message);

        // Both sides must be named. An error that says only "version mismatch" leaves the operator to
        // find which of sixty-seven packages lagged.
        Assert.Contains("Themia.AspNetCore.DataProtection", message);
        Assert.Contains("0.15.0", message);
        Assert.Contains("0.16.1", message);
    }

    [Fact]
    public void Refuses_across_a_major()
    {
        Assert.NotNull(MigrationAssemblyVersion.Describe(
            Runner, "Themia.Data.Migrations", new Version(1, 0, 0), "Themia.Modules.Identity"));
    }

    [Fact]
    public void Allows_a_patch_difference()
    {
        // Deliberate. Themia ships one version for everything, so in principle any difference is
        // unsupported — but the minor is where a pre-1.0 breaking change lands, and refusing a patch lag
        // would turn a harmless mix into a failed boot with nothing to point at.
        Assert.Null(MigrationAssemblyVersion.Describe(
            Runner, "Themia.Data.Migrations", new Version(0, 16, 0), "Themia.Modules.Identity"));
    }

    [Fact]
    public void Ignores_an_assembly_that_is_not_ours()
    {
        // Consumers pass their own migration assemblies to Run — that is the documented way to use it —
        // and their versioning has nothing to do with ours. Checking them would break every consumer on
        // the first upgrade.
        Assert.Null(MigrationAssemblyVersion.Describe(
            Runner, "Themia.Data.Migrations", new Version(3, 2, 1), "Propertiezy.Migrations"));
    }

    [Fact]
    public void Ignores_a_name_that_merely_starts_with_themia()
    {
        // "ThemiaHelpers" is not "Themia.Helpers"; the dot is part of the prefix on purpose.
        Assert.Null(MigrationAssemblyVersion.Describe(
            Runner, "Themia.Data.Migrations", new Version(3, 2, 1), "ThemiaExtensionsByAConsumer"));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void Fails_open_when_a_version_cannot_be_read(bool runnerNull, bool assemblyNull)
    {
        // A missing attribute is not evidence of a mismatch. This guard must never be the reason a
        // correctly-versioned deployment cannot boot.
        Assert.Null(MigrationAssemblyVersion.Describe(
            runnerNull ? null : Runner,
            "Themia.Data.Migrations",
            assemblyNull ? null : new Version(0, 15, 0),
            "Themia.Modules.Identity"));
    }

    [Fact]
    public void Reads_the_informational_version_without_its_build_metadata()
    {
        // SourceLink appends "+<sha>" and a prerelease would append "-rc.1"; neither parses as a Version,
        // and falling back to the assembly version would silently compare a different number.
        var version = MigrationAssemblyVersion.Read(typeof(ThemiaMigrations).Assembly);

        Assert.NotNull(version);
        Assert.Equal(ThisVersion().Major, version.Major);
        Assert.Equal(ThisVersion().Minor, version.Minor);
    }

    [Fact]
    public void The_real_assemblies_agree_with_each_other()
    {
        // The whole repo is the fixture: every Themia assembly loaded here must pass its own guard, so a
        // future package that forgets the shared version breaks this rather than a consumer's boot.
        MigrationAssemblyVersion.Verify(typeof(ThemiaMigrations).Assembly);
    }

    private static Version ThisVersion() =>
        typeof(ThemiaMigrations).Assembly.GetName().Version ?? new Version(0, 0);
}
