using System.Globalization;
using System.Text;

namespace Themia.Exceptional.Alerts;

/// <summary>Renders alert candidates into one plain-text digest within Telegram's message-size limit.</summary>
internal static class AlertDigestBuilder
{
    // Telegram rejects text over 4096 characters; stay clear of the edge so counting differences never matter.
    internal const int MaxMessageLength = 4000;
    private const int MaxTypeLength = 120;
    private const int MaxMessageTextLength = 200;
    private const string DateFormat = "yyyy-MM-dd HH:mm'Z'";

    public static AlertDigest Build(
        IReadOnlyList<ExceptionAlertCandidate> candidates, string applicationName, ExceptionAlertOptions options)
    {
        var header = $"[{options.EnvironmentName}] {applicationName}: {candidates.Count} exception alert(s)\n";
        var text = new StringBuilder(header);
        var included = 0;

        foreach (var candidate in candidates)
        {
            var block = RenderGroup(included + 1, candidate, options);
            var remaining = candidates.Count - included - 1;
            // Reserve room for the "+N more" footer so it can always be appended.
            if (text.Length + block.Length + MoreLine(remaining).Length > MaxMessageLength && included > 0)
                break;
            text.Append(block);
            included++;
        }

        if (included < candidates.Count)
            text.Append(MoreLine(candidates.Count - included));
        return new AlertDigest(text.ToString(), included);
    }

    private static string MoreLine(int count) => count > 0 ? $"\n+{count} more" : string.Empty;

    private static string RenderGroup(int index, ExceptionAlertCandidate c, ExceptionAlertOptions options)
    {
        var type = Clean(c.Type, MaxTypeLength);
        var message = Clean(c.Message, MaxMessageTextLength);
        var link = options.AdminUrlTemplate.Replace("{guid}", c.Guid.ToString("D"), StringComparison.Ordinal);
        return string.Create(CultureInfo.InvariantCulture,
            $"\n{index}) {c.Trigger.ToString().ToUpperInvariant()} {type}\n{message}\ncount {c.DuplicateCount}, first {c.FirstSeenUtc.ToString(DateFormat, CultureInfo.InvariantCulture)}, last {c.LastSeenUtc.ToString(DateFormat, CultureInfo.InvariantCulture)}\n{link}\n");
    }

    // Masks personal data and tokens, then flattens whitespace so exception text cannot fake extra lines.
    private static string Clean(string value, int maxLength)
    {
        var masked = AlertMessageMasker.Mask(value);
        var flat = string.Join(' ', masked.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return flat.Length <= maxLength ? flat : flat[..maxLength] + "...";
    }
}
