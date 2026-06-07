using System.Text.Encodings.Web;
using System.Text.Json;

namespace Themia.Quartz.Dashboard.Json
{
    /// <summary>
    /// Shared System.Text.Json options for the dashboard. Both are PascalCase
    /// (<c>PropertyNamingPolicy = null</c>) with nulls included, matching the Newtonsoft defaults the
    /// templates/JS depend on. The encoder is the only axis that varies:
    /// <list type="bullet">
    /// <item><see cref="Default"/> — JSON written to HTTP responses (consumed via <c>JSON.parse</c>).
    /// Uses the default encoder, which escapes <c>&lt; &gt; &amp; +</c> and non-ASCII (decoded back by
    /// <c>JSON.parse</c>).</item>
    /// <item><see cref="RawInject"/> — JSON injected RAW into HTML/JS templates (WriteSafeString /
    /// triple-stache). Uses <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/> so those
    /// characters are emitted literally, matching the pre-migration Newtonsoft behavior. Admin-only
    /// dashboard — parity, not a new XSS surface.</item>
    /// </list>
    /// Pick <see cref="RawInject"/> for anything emitted through a template without <c>JSON.parse</c>;
    /// pick <see cref="Default"/> for everything returned as an <c>application/json</c> response.
    /// </summary>
    internal static class DashboardJsonOptions
    {
        public static readonly JsonSerializerOptions Default = CreateFrozen(JavaScriptEncoder.Default);

        public static readonly JsonSerializerOptions RawInject = CreateFrozen(JavaScriptEncoder.UnsafeRelaxedJsonEscaping);

        // MakeReadOnly() up front so these shared instances can't be mutated by a caller before first
        // use (which would silently break wire-format parity for every consumer). A mutation attempt
        // now fails fast with InvalidOperationException instead.
        private static JsonSerializerOptions CreateFrozen(JavaScriptEncoder encoder)
        {
            var options = new JsonSerializerOptions { Encoder = encoder };
            options.MakeReadOnly(populateMissingResolver: true);
            return options;
        }
    }
}
