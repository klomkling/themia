namespace Themia.Challenges;

/// <summary>
/// Identity of a challenge. Tenant is part of it deliberately: two tenants may hold the same phone
/// number, so without it a code issued to one tenant would verify under another.
/// </summary>
/// <param name="Key">The opaque key — a phone number, an email address, a user id. Never parsed.</param>
/// <param name="Purpose">Scopes the challenge and selects its configuration.</param>
/// <param name="TenantId">The owning tenant, or <see langword="null"/> for a platform-level challenge.</param>
public sealed record ChallengeScope(string Key, string Purpose, string? TenantId = null)
{
    /// <summary>
    /// The longest accepted <see cref="Key"/>, matching the <c>key</c> column width in the schema.
    /// SQL Server caps an indexed nvarchar key at 450 characters, which is what sets the number.
    /// </summary>
    public const int MaxKeyLength = 450;

    /// <summary>The longest accepted <see cref="Purpose"/>, matching the <c>purpose</c> column width.</summary>
    public const int MaxPurposeLength = 100;

    /// <summary>The longest accepted <see cref="TenantId"/>, matching the <c>tenant_id</c> column width.</summary>
    public const int MaxTenantIdLength = 100;

    /// <summary>
    /// The <see cref="Key"/> carried by a <see cref="ChallengeVerifyResult"/> whose challenge was never
    /// resolved — every failing outcome of <see cref="IChallengeService.VerifyByTokenAsync"/>.
    /// </summary>
    /// <remarks>
    /// A token lookup that finds nothing has learned nothing, and <see cref="Key"/> cannot be empty. The
    /// alternative — echoing back some part of the caller's input — would read like a resolved key and
    /// invite a caller to act on it. Compare against this constant rather than testing the outcome if you
    /// need to know whether <see cref="ChallengeVerifyResult.Scope"/> is real.
    /// </remarks>
    public const string UnresolvedKey = "__themia_unresolved__";

    /// <summary>
    /// The opaque key — a phone number, an email address, a user id. Never parsed, but bounded: a key
    /// longer than <see cref="MaxKeyLength"/> is rejected here rather than reaching a dialect.
    /// </summary>
    /// <remarks>
    /// Rejecting at the boundary is deliberate. Two dialect implementations independently ran into this
    /// property being unbounded, and the failure it produces is silent rather than loud: an over-long key
    /// that reaches storage is truncated to the column width, which makes it a <b>different rate-limit
    /// bucket than the caller intended</b>. The per-key ceiling is the layer that bounds an SMS bill, so
    /// a miscounted bucket disables that protection with nothing logged and nothing thrown. MySQL will do
    /// exactly this under an <c>IGNORE</c>-style statement regardless of <c>sql_mode</c>, so a dialect
    /// cannot reliably defend against it either. One check here removes the class of defect for every
    /// engine at once.
    /// </remarks>
    public string Key
    {
        get => key;
        // Validated in the accessor, not just from the constructor: a record's `with` expression copies
        // fields through the copy constructor and then runs init accessors, so validation placed only on
        // the positional parameter would be bypassed by `scope with { Key = <over-long> }`.
        init => key = Require(value, MaxKeyLength, nameof(Key));
    }

    /// <summary>Scopes the challenge and selects its configuration.</summary>
    public string Purpose
    {
        get => purpose;
        init => purpose = Require(value, MaxPurposeLength, nameof(Purpose));
    }

    /// <summary>The owning tenant, or <see langword="null"/> for a platform-level challenge.</summary>
    public string? TenantId
    {
        get => tenantId;
        init => tenantId = value is null ? null : Require(value, MaxTenantIdLength, nameof(TenantId));
    }

    private readonly string key = Require(Key, MaxKeyLength, nameof(Key));
    private readonly string purpose = Require(Purpose, MaxPurposeLength, nameof(Purpose));
    private readonly string? tenantId = TenantId is null ? null : Require(TenantId, MaxTenantIdLength, nameof(TenantId));

    private static string Require(string value, int maxLength, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, name);

        if (value.Length > maxLength)
        {
            throw new ArgumentException(
                $"Must be at most {maxLength} characters (was {value.Length}) to fit the column it is stored in. "
                + "An over-long value would be truncated by the store into a different rate-limit bucket than intended.",
                name);
        }

        return value;
    }

    /// <summary>
    /// Renders the scope for logs and diagnostics with <see cref="Key"/> masked. A positional record's
    /// compiler-generated <c>ToString()</c> prints every property including <see cref="Key"/> — a phone
    /// number or an email address, i.e. PII — which a "{Scope}" log template would then leak verbatim.
    /// Only the last 4 characters survive, enough to correlate a scope across log lines without
    /// reconstructing the key. <see cref="Purpose"/> and <see cref="TenantId"/> are not sensitive and
    /// are rendered in full.
    /// </summary>
    /// <returns>A string representation safe to log.</returns>
    public override string ToString() =>
        $"ChallengeScope {{ Key = {MaskKey(Key)}, Purpose = {Purpose}, TenantId = {TenantId ?? "(none)"} }}";

    // Same masking shape as Themia.Notifications.Providers.RecipientRedaction.Mask: keep the last 4
    // characters for correlation, mask the rest. Reimplemented here rather than referenced because
    // that type is internal to Themia.Notifications, and Themia.Challenges has no dependency on it.
    private static string MaskKey(string? key)
    {
        if (string.IsNullOrEmpty(key))
        {
            return "(none)";
        }

        return key.Length <= 4 ? "****" : new string('*', key.Length - 4) + key[^4..];
    }
}
