namespace Themia.WebAuthn;

/// <summary>
/// Decides whether an assertion's signature counter is consistent with the one already recorded.
/// </summary>
/// <remarks>
/// WebAuthn §7.2 step 21. An authenticator increments this on every assertion, so a value that does not
/// move forward means two authenticators are answering for one credential — the private key has been
/// extracted and copied.
/// <para>
/// This is checked here because the library reports the counter and takes no position on it, and an
/// integration that ignores it looks completely healthy: every sign-in succeeds, including the cloned
/// one. It is the WebAuthn counterpart of the TOTP replay guard.
/// </para>
/// </remarks>
public static class SignCounterPolicy
{
    /// <summary>Whether an assertion's counter is acceptable against the stored one.</summary>
    /// <param name="storedSignCount">The counter recorded at the last successful assertion.</param>
    /// <param name="presentedSignCount">The counter in the assertion just verified.</param>
    /// <returns><see langword="false"/> when the credential appears to have been cloned.</returns>
    public static bool IsAcceptable(uint storedSignCount, uint presentedSignCount)
    {
        // Both zero: the authenticator does not implement a counter. The spec permits this and many
        // platform authenticators do it, so treating it as a clone would lock out every such user on
        // their second sign-in.
        if (storedSignCount == 0 && presentedSignCount == 0)
        {
            return true;
        }

        return presentedSignCount > storedSignCount;
    }
}
