using QRCoder;

namespace Themia.SecondFactor;

/// <summary>Renders a URI as an SVG QR code in-process, so adopters' web apps need no JavaScript QR library and no network.</summary>
internal static class QrSvg
{
    private const int PixelsPerModule = 4;

    public static string Render(Uri uri)
    {
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(uri.AbsoluteUri, QRCodeGenerator.ECCLevel.M);
        return new SvgQRCode(data).GetGraphic(PixelsPerModule);
    }
}
