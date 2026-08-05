using System.Text;
using Themia.Modules.Identity.Abstractions;

namespace Themia.Modules.Identity.Services;

/// <summary>
/// The default <see cref="IPhoneNumberNormalizer"/>: strips formatting and nothing else. Keeps a single
/// leading <c>+</c> and the digits; discards spaces, dashes, dots, slashes and parentheses.
/// </summary>
/// <remarks>
/// <para>
/// <b>It deliberately does not understand phone numbers.</b> <c>+66 81 111 2222</c> and
/// <c>+66811112222</c> normalize to the same value, because those differ only in formatting.
/// <c>0811112222</c> and <c>+66811112222</c> do <b>not</b>, because they are the same number only if you
/// know the default region — and a framework default that guessed one would silently merge two different
/// people's accounts in every deployment where the guess was wrong.
/// </para>
/// <para>
/// The practical consequence, which belongs in your registration UI rather than in a support ticket: a
/// user must log in with the number in the form they registered it. If you want <c>08…</c> to mean
/// <c>+668…</c>, that is a real requirement and a real normalizer — implement
/// <see cref="IPhoneNumberNormalizer"/> over a library that does E.164 with your region, register it, and
/// re-normalize existing rows. Do not try to reach it by loosening this one.
/// </para>
/// <para>
/// Only the FIRST character may be a <c>+</c>: a later one is formatting noise, and treating it as
/// significant would make <c>+66-81+111</c> its own distinct number.
/// </para>
/// </remarks>
public sealed class FormattingOnlyPhoneNumberNormalizer : IPhoneNumberNormalizer
{
    /// <inheritdoc />
    public string? Normalize(string? phoneNumber)
    {
        if (string.IsNullOrWhiteSpace(phoneNumber))
        {
            return null;
        }

        var trimmed = phoneNumber.Trim();
        var builder = new StringBuilder(trimmed.Length);

        for (var i = 0; i < trimmed.Length; i++)
        {
            var c = trimmed[i];
            if (char.IsAsciiDigit(c))
            {
                builder.Append(c);
            }
            else if (c == '+' && builder.Length == 0)
            {
                builder.Append(c);
            }
        }

        // "+" alone, or an input that was pure punctuation, carries no number. Null rather than "" so it
        // lands outside the filtered unique indexes instead of colliding with every other empty value.
        var normalized = builder.ToString();
        return normalized is "" or "+" ? null : normalized;
    }
}
