using System.Text.RegularExpressions;

namespace Themia.Conversations.Internal;

/// <summary>
/// The default masker: for a guest viewer, e-mail addresses, phone numbers and chat handles in the text are replaced. Best effort. Every pattern has a match timeout,
/// and a timeout surfaces as an exception, which the store treats as a failure to mask (the body is hidden). <c>\d</c> matches every Unicode digit, so Thai digits are masked too.
/// </summary>
internal sealed class RegexConversationMasker : IConversationMasker
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(100);
    private const RegexOptions Flags = RegexOptions.CultureInvariant | RegexOptions.IgnoreCase;

    // No lookbehind in the first three, so they can run without backtracking (linear time); the handle pattern needs a lookbehind and keeps the timeout.
    private const RegexOptions Linear = Flags | RegexOptions.NonBacktracking;

    private static readonly (Regex Pattern, string Replacement)[] Rules =
    [
        (new Regex(@"[^\s@<>]+@[^\s@<>]+\.[^\s@<>]+", Linear, Timeout), "[email hidden]"),
        (new Regex(@"\+?\d(?:[\s().\-]?\d){6,}", Linear, Timeout), "[phone hidden]"),
        (new Regex(@"\bline(?:\s+id)?\s*[:=]\s*\S+", Linear, Timeout), "[handle hidden]"),
        (new Regex(@"(?<![\w@])@\w{2,}", Flags, Timeout), "[handle hidden]"),
    ];

    public ConversationMessage Mask(ConversationActorKind viewer, ConversationMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (viewer != ConversationActorKind.Guest || message.Body is null)
        {
            return message;
        }

        var body = message.Body;
        foreach (var (pattern, replacement) in Rules)
        {
            body = pattern.Replace(body, replacement);
        }

        return message with { Body = body };
    }
}
