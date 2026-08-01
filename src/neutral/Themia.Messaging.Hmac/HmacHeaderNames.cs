namespace Themia.Messaging.Hmac;

/// <summary>The five wire header names, derived from a per-peer prefix.</summary>
/// <remarks>
/// Header names are NOT part of the canonical string, so a mismatch can only cause a failure to verify,
/// never a bypass — which is why the prefix is safe to make configurable where canonicalization is not.
/// It exists because the live ezy-assets/propertiezy link sends <c>X-Propertiezy-*</c>: with a different
/// prefix a receiver looks for a header that is not there and rejects a perfectly valid signature.
/// </remarks>
/// <param name="Prefix">The header prefix, e.g. <c>X-Themia-</c>.</param>
public sealed record HmacHeaderNames(string Prefix)
{
    /// <summary>The default prefix used when a peer does not override it.</summary>
    public const string DefaultPrefix = "X-Themia-";

    // Computed from Prefix rather than captured once in field initializers: the latter let
    // `names with { Prefix = "X-Foo-" }` compile and produce a record with a new Prefix sitting beside
    // the OLD derived names — internally inconsistent state a `with` expression should never be able to
    // produce. Computed properties keep `with` coherent by construction.

    /// <summary>Header carrying the signed timestamp. Required.</summary>
    public string Timestamp => Prefix + "Timestamp";

    /// <summary>Header carrying the lowercase-hex signature. Required.</summary>
    public string Signature => Prefix + "Signature";

    /// <summary>Header selecting which inbound key verifies. Optional.</summary>
    public string KeyId => Prefix + "Key-Id";

    /// <summary>Header naming the signing scheme. Optional; absence means <c>themia-hmac-v1</c>.</summary>
    public string Scheme => Prefix + "Scheme";

    /// <summary>Header naming the originating system, for the loop guard. Optional.</summary>
    public string Origin => Prefix + "Origin";
}
