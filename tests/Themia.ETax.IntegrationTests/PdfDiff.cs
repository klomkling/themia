using System.Text;
using System.Text.RegularExpressions;
using Xunit;

namespace Themia.ETax.IntegrationTests;

/// <summary>Explains a byte mismatch precisely enough to act on, and keeps both files for the CI artifact.</summary>
internal static partial class PdfDiff
{
    public static int PageCount(byte[] pdf) => PageObject().Matches(Fixtures.Latin1(pdf)).Count;

    /// <summary>Offsets of the values of /CreationDate and /ModDate: the only bytes allowed to differ between raw renders.</summary>
    public static HashSet<int> DateValueOffsets(byte[] pdf)
    {
        var text = Fixtures.Latin1(pdf);
        var offsets = new HashSet<int>();
        foreach (Match m in DateField().Matches(text))
            for (var i = 0; i < m.Groups[1].Length; i++)
                offsets.Add(m.Groups[1].Index + i);
        return offsets;
    }

    public static void AssertIdentical(string name, byte[] a, byte[] b, string browserVersion)
    {
        if (a.AsSpan().SequenceEqual(b))
            return;

        var at = FirstDifference(a, b);
        SaveBoth(name, a, b);
        Assert.Fail(
            $"[{name}] PDFs differ. Browser {browserVersion}. Sizes {a.Length}/{b.Length}. " +
            $"First difference at offset {at} ({Classify(a, b, at)}).\nA: {Context(a, at)}\nB: {Context(b, at)}\n" +
            $"Both files are in {OutputDir()}.");
    }

    private static int FirstDifference(byte[] a, byte[] b)
    {
        var n = Math.Min(a.Length, b.Length);
        for (var i = 0; i < n; i++)
            if (a[i] != b[i])
                return i;
        return n;
    }

    private static string Classify(byte[] a, byte[] b, int at)
    {
        if (a.Length != b.Length) return "length differs";
        var around = Fixtures.Latin1(a[Math.Max(0, at - 200)..Math.Min(a.Length, at + 200)]);
        if (around.Contains("/CreationDate") || around.Contains("/ModDate")) return "date field";
        if (around.Contains("/FontName") || around.Contains("/BaseFont")) return "font object name";
        if (around.Contains("/Subtype /Image") || around.Contains("/DCTDecode")) return "image stream";
        return "content stream";
    }

    private static string Context(byte[] x, int at)
    {
        var from = Math.Max(0, at - 32);
        var slice = x[from..Math.Min(x.Length, at + 32)];
        var ascii = new StringBuilder();
        foreach (var b in slice)
            ascii.Append(b is >= 0x20 and < 0x7F ? (char)b : '.');
        return $"{Convert.ToHexString(slice)}  |{ascii}|";
    }

    private static void SaveBoth(string name, byte[] a, byte[] b)
    {
        var dir = OutputDir();
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, $"{name}-a.pdf"), a);
        File.WriteAllBytes(Path.Combine(dir, $"{name}-b.pdf"), b);
    }

    private static string OutputDir()
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "Themia.sln")))
            root = root.Parent;
        return Path.Combine(root?.FullName ?? AppContext.BaseDirectory, "TestResults", "etax-pdf-diff");
    }

    [GeneratedRegex(@"/Type\s*/Page(?![A-Za-z])")]
    private static partial Regex PageObject();

    [GeneratedRegex(@"/(?:CreationDate|ModDate) \((D:[^)]*)\)")]
    private static partial Regex DateField();
}
