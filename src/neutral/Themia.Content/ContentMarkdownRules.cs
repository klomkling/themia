using System.Globalization;
using System.Text.RegularExpressions;

using Markdig;
using Markdig.Syntax;
using Markdig.Syntax.Inlines;

namespace Themia.Content;

/// <summary>The two ways markdown can be refused at write.</summary>
public enum ContentMarkdownViolationKind
{
    /// <summary>An HTML block or inline HTML outside code: a tag, a comment, a declaration or a processing
    /// instruction.</summary>
    RawHtml,

    /// <summary>A link, image, autolink or reference-definition destination whose scheme is not <c>http</c>,
    /// <c>https</c>, <c>mailto</c> or <c>tel</c>.</summary>
    DisallowedUrl,
}

/// <summary>One reason markdown was refused.</summary>
/// <param name="Kind">What was refused.</param>
/// <param name="Detail">The offending tag or destination, for the author's error message.</param>
public sealed record ContentMarkdownViolation(ContentMarkdownViolationKind Kind, string Detail);

/// <summary>
/// The write half of Themia.Content's markdown safety: markdown is parsed with Markdig and refused when its
/// syntax tree contains raw HTML or a destination with an unsafe scheme.
/// </summary>
/// <remarks>
/// <para><b>Refused, not sanitised.</b> A sanitiser over markdown corrupts it; refusing tells the author what is
/// wrong and leaves their text as written. It is an allow-list by construction — there is no list of dangerous
/// tags or schemes to keep current.</para>
/// <para><b>A parser, not regular expressions.</b> Removing code with regular expressions disagreed with
/// CommonMark in both directions: it rejected an indented code block and accepted a <c>javascript:</c> link after
/// a fence closed by a longer fence. Code blocks and code spans are not HTML nodes in Markdig's tree, so HTML
/// inside code is accepted and renders escaped.</para>
/// <para><b>This is not the safety boundary.</b> The web renderer drops raw HTML and refuses the same schemes
/// again at render, because this rule cannot fix rows saved before it existed.</para>
/// </remarks>
public static class ContentMarkdownRules
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder().Build();

    private static readonly HashSet<string> AllowedSchemes =
        new(StringComparer.OrdinalIgnoreCase) { "http", "https", "mailto", "tel" };

    private static readonly Regex Scheme =
        new("^([a-z][a-z0-9+.-]*):", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);

    private static readonly Regex HexEntity =
        new("&#x([0-9a-f]+);?", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, RegexTimeout);

    private static readonly Regex DecimalEntity =
        new("&#([0-9]+);?", RegexOptions.CultureInvariant, RegexTimeout);

    /// <summary>Every violation in <paramref name="markdown"/>; empty when it may be saved.</summary>
    /// <param name="markdown">The markdown source as the author wrote it.</param>
    /// <exception cref="ArgumentNullException"><paramref name="markdown"/> is <see langword="null"/>.</exception>
    public static IReadOnlyList<ContentMarkdownViolation> Check(string markdown)
    {
        ArgumentNullException.ThrowIfNull(markdown);

        var document = Markdown.Parse(markdown, Pipeline);
        var violations = new List<ContentMarkdownViolation>();

        foreach (var block in document.Descendants<HtmlBlock>())
        {
            violations.Add(new ContentMarkdownViolation(ContentMarkdownViolationKind.RawHtml, block.Lines.ToString()));
        }

        foreach (var inline in document.Descendants<HtmlInline>())
        {
            violations.Add(new ContentMarkdownViolation(ContentMarkdownViolationKind.RawHtml, inline.Tag));
        }

        foreach (var link in document.Descendants<LinkInline>())
        {
            AddIfDisallowed(link.Url, violations);
        }

        foreach (var autolink in document.Descendants<AutolinkInline>())
        {
            AddIfDisallowed(autolink.Url, violations);
        }

        foreach (var definition in document.Descendants<LinkReferenceDefinition>())
        {
            AddIfDisallowed(definition.Url, violations);
        }

        return violations;
    }

    /// <summary>
    /// Whether a destination may be linked: it has no scheme (a relative path, <c>/path</c>, <c>#fragment</c> or
    /// <c>?query</c>), or its scheme is <c>http</c>, <c>https</c>, <c>mailto</c> or <c>tel</c>.
    /// </summary>
    /// <remarks>
    /// Normalised the way a browser reads an attribute before deciding: HTML entities are decoded (with or
    /// without the terminating semicolon) and ASCII whitespace and control characters are removed, so
    /// <c>java&amp;#9;script:</c> is recognised as <c>javascript:</c>. Markdig has usually decoded entities already;
    /// decoding again can only make the check stricter. This mirrors <c>urlAllowed</c> in the renderer
    /// configuration pinned by the golden fixture.
    /// </remarks>
    /// <param name="url">The destination; <see langword="null"/> or empty is allowed.</param>
    public static bool IsUrlAllowed(string? url)
    {
        var normalised = RemoveWhitespaceAndControl(DecodeEntities(url ?? string.Empty));
        var match = Scheme.Match(normalised);
        return !match.Success || AllowedSchemes.Contains(match.Groups[1].Value);
    }

    private static void AddIfDisallowed(string? url, List<ContentMarkdownViolation> violations)
    {
        if (!IsUrlAllowed(url))
        {
            violations.Add(new ContentMarkdownViolation(ContentMarkdownViolationKind.DisallowedUrl, url ?? string.Empty));
        }
    }

    private static string DecodeEntities(string value)
    {
        var decoded = HexEntity.Replace(value, m => FromCodePoint(m.Groups[1].Value, NumberStyles.HexNumber));
        decoded = DecimalEntity.Replace(decoded, m => FromCodePoint(m.Groups[1].Value, NumberStyles.Integer));
        return decoded
            .Replace("&colon;", ":", StringComparison.OrdinalIgnoreCase)
            .Replace("&tab;", "\t", StringComparison.OrdinalIgnoreCase)
            .Replace("&newline;", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("&amp;", "&", StringComparison.OrdinalIgnoreCase);
    }

    // An out-of-range or surrogate code point decodes to nothing. That can only join the characters on either
    // side, which makes a disguised scheme easier to recognise, never harder.
    private static string FromCodePoint(string digits, NumberStyles style) =>
        int.TryParse(digits, style, CultureInfo.InvariantCulture, out var codePoint)
            && codePoint is > 0 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF)
                ? char.ConvertFromUtf32(codePoint)
                : string.Empty;

    private static string RemoveWhitespaceAndControl(string value) =>
        string.Concat(value.Where(c => c > ' ' && c != (char)127));
}
