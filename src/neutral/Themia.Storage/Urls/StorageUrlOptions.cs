namespace Themia.Storage.Urls;

/// <summary>How <see cref="IStorageUrlService"/> turns a provider's presigned URL into one a client can use.</summary>
public sealed class StorageUrlOptions
{
    /// <summary>
    /// The absolute URL of the mount that serves Local presigned transfers — the prefix passed to
    /// <c>MapThemiaLocalStorage</c>, on the host clients reach, e.g. <c>https://api.example.com/api/v1/storage</c>.
    /// </summary>
    /// <remarks>
    /// Required only when the registered provider returns relative URLs, which the Local provider does
    /// (<c>_local/get?key=…&amp;token=…</c>). An S3-compatible provider signs absolute URLs, and those are handed
    /// back untouched.
    /// <para>
    /// "Presigned", not "download": the same mount serves <c>_local/put</c> as well as <c>_local/get</c>.
    /// </para>
    /// </remarks>
    public string PresignedBaseUrl { get; set; } = string.Empty;

    /// <summary>Reports the first configuration problem, or null when usable.</summary>
    internal string? Validate()
    {
        if (string.IsNullOrWhiteSpace(PresignedBaseUrl))
        {
            return null;   // legitimate for S3; the Local case fails at the call that needs it, naming this option
        }

        // Uri.TryCreate(..., Absolute) alone accepts "/api/v1/storage" on Unix as file:///api/v1/storage, so the
        // scheme is checked explicitly — the same trap LocalStorageOptions.PublicBaseUrl guards.
        if (!Uri.TryCreate(PresignedBaseUrl, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return $"PresignedBaseUrl must be an absolute http(s) URL, e.g. https://api.example.com/api/v1/storage, but was '{PresignedBaseUrl}'.";
        }

        if (!string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
        {
            return $"PresignedBaseUrl must not carry a query or fragment, but was '{PresignedBaseUrl}'.";
        }

        return null;
    }
}
