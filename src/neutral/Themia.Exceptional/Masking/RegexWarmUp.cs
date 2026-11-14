using System.Text.RegularExpressions;

namespace Themia.Exceptional.Masking;

/// <summary>
/// Runs each compiled masking regex once when its masker is first used. A compiled <see cref="Regex"/> is JIT-compiled
/// on its first match, and that time counts against the match timeout; on a cold process it can exceed the timeout and
/// turn the first masked value into <c>***</c> (fail closed, but a value that did not need masking is lost).
/// </summary>
internal static class RegexWarmUp
{
    private const string Sample = "a@b.co Bearer abcdefgh Authorization: x api_key=x eyJa.b.c 12345678 " +
                                  "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdef012345 ABCDEFGHIJKLMNOPQRSTUVWXYZabcdef+/==";

    public static void Run(Regex[] patterns)
    {
        foreach (var pattern in patterns)
        {
            try
            {
                pattern.IsMatch(Sample);
            }
            catch (RegexMatchTimeoutException)
            {
                // The JIT work is done by the time the timeout fires; real calls start warm.
            }
        }
    }
}
