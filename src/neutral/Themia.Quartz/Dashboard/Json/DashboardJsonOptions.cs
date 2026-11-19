using System.Text.Encodings.Web;
using System.Text.Json;

namespace Themia.Quartz.Dashboard.Json
{
    /// <summary>
    /// Shared System.Text.Json options for the dashboard. Both are PascalCase
    /// (<c>PropertyNamingPolicy = null</c>) with nulls included, matching the Newtonsoft defaults the
    /// templates/JS depend on. The encoder is the only axis that varies:
    /// <list type="bullet">
    /// <item><see cref="Default"/> — the only choice for JSON that can hold user-controlled text (job,
    /// trigger, group and calendar names, data-map values): HTTP responses and the <c>{{Json}}</c> /
    /// <c>PageData</c> template helpers. It escapes <c>&lt; &gt; &amp; + ' "</c> and non-ASCII as
    /// <c>\uXXXX</c>, so a value can never close a <c>&lt;script&gt;</c> block, and JavaScript decodes it back.</item>
    /// <item><see cref="RawInject"/> — literal characters (<see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/>).
    /// Only for values the server itself defines (static labels, CLR type ids) that are injected raw.
    /// Never pass it a value an operator or caller can set: it leaves <c>&lt;</c> unescaped.</item>
    /// </list>
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
