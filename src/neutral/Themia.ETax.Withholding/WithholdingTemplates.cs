namespace Themia.ETax.Withholding;

/// <summary>The certificate template versions. A template change ships as a NEW version, never an edit.</summary>
public static class WithholdingTemplates
{
    /// <summary>The version new certificates are issued under.</summary>
    public const string CurrentVersion = "2";

    private static readonly Lazy<string> Version1 = new(() => ReadResource("Templates.withholding-certificate.v1.hbs"));
    private static readonly Lazy<string> Version2 = new(() => ReadResource("Templates.withholding-certificate.v2.hbs"));

    /// <summary>Whether <paramref name="templateVersion"/> is a version this release can still render.</summary>
    /// <param name="templateVersion">The version stored with a certificate.</param>
    public static bool IsSupported(string? templateVersion) => templateVersion is "1" or "2";

    internal static string Get(string templateVersion) => templateVersion switch
    {
        "1" => Version1.Value,
        "2" => Version2.Value,
        _ => throw new ArgumentException($"Unknown withholding certificate template version '{templateVersion}'.", nameof(templateVersion)),
    };

    private static string ReadResource(string name)
    {
        using var stream = typeof(WithholdingTemplates).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Themia.ETax.Withholding: missing embedded resource {name}.");
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
