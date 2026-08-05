namespace Themia.Modules.Identity.Abstractions;

/// <summary>
/// Reduces a phone number to the form used for equality, uniqueness and lookup — the value stored in
/// <see cref="Entities.User.NormalizedPhoneNumber"/>.
/// </summary>
/// <remarks>
/// <para>
/// Injectable because <b>"the same number" is a deployment decision, not a universal one.</b> Whether
/// <c>0811112222</c> and <c>+66811112222</c> are one number depends on a default region the framework has
/// no way to know. Guessing it wrong in either direction is harmful: guess that they differ and a user
/// cannot log in with the number they think they have; guess that they match and two different people in
/// two countries can collapse onto one account.
/// </para>
/// <para>
/// <b>Whatever you implement must be stable.</b> The output is persisted and uniquely indexed, so
/// changing the rule silently orphans every row normalized under the old one — those users stop being
/// findable by their own number, and a new registration can take a number that is already in use under a
/// different normal form. Treat a change as a data migration, not a configuration tweak.
/// </para>
/// </remarks>
public interface IPhoneNumberNormalizer
{
    /// <summary>Reduces <paramref name="phoneNumber"/> to its comparison form.</summary>
    /// <param name="phoneNumber">The number as entered.</param>
    /// <returns>
    /// The comparison form, or <see langword="null"/> when the input carries no number at all — which is
    /// stored as <see langword="null"/> and therefore matches nothing, rather than colliding with every
    /// other unusable value on the empty string.
    /// </returns>
    string? Normalize(string? phoneNumber);
}
