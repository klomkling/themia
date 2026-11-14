using System.Text.RegularExpressions;
using Themia.Exceptional.Alerts;
using Themia.Exceptional.Masking;
using Xunit;

namespace Themia.Exceptional.Tests;

/// <summary>A value that defeats a masker (regex match timeout) must be dropped whole, never passed through.</summary>
public class MaskerTimeoutTests
{
    // Catastrophic backtracking on a non-matching tail, with a timeout short enough to always fire.
    private static readonly Regex[] TimingOut = [new("(a+)+$", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(1))];
    private static readonly string Hostile = new string('a', 40) + "!";

    [Fact]
    public void CredentialShapeMasker_ReplacesTheWholeValue_WhenAPatternTimesOut() =>
        Assert.Equal("***", CredentialShapeMasker.Mask(Hostile, TimingOut));

    [Fact]
    public void AlertMessageMasker_ReplacesTheWholeValue_WhenAPatternTimesOut() =>
        Assert.Equal("***", AlertMessageMasker.Mask(Hostile, TimingOut));
}
