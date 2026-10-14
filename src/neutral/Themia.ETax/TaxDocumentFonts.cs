using System.Reflection;
using System.Text;

namespace Themia.ETax;

/// <summary>The embedded Sarabun faces and their licence. Nothing is read from the host, so output does not depend on runner fonts.</summary>
internal static class TaxDocumentFonts
{
    private static readonly Lazy<byte[]> RegularBytes = new(() => ReadBytes("Fonts.Sarabun-Regular.ttf"));
    private static readonly Lazy<byte[]> BoldBytes = new(() => ReadBytes("Fonts.Sarabun-Bold.ttf"));
    private static readonly Lazy<string> License = new(() => Encoding.UTF8.GetString(ReadBytes("Fonts.OFL.txt")));

    public static byte[] Regular => RegularBytes.Value;

    public static byte[] Bold => BoldBytes.Value;

    public static string LicenseText => License.Value;

    internal static byte[] ReadBytes(string resourceName)
    {
        using var stream = typeof(TaxDocumentFonts).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Themia.ETax: embedded resource '{resourceName}' is missing.");
        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        return buffer.ToArray();
    }
}
