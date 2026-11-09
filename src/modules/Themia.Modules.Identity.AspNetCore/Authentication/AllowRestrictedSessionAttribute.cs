namespace Themia.Modules.Identity.AspNetCore.Authentication;

/// <summary>
/// Marks an endpoint that a restricted session may call. A session is restricted after a recovery-code sign-in or when the user's
/// policy requires a stronger second factor; until it enrols one it may reach only the endpoints that carry this attribute
/// (enrolment, sign-out, the user's own methods, the endpoints a client needs to boot). Needs
/// <c>AddThemiaRestrictedSessionGate</c>; without it the attribute has no effect.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class AllowRestrictedSessionAttribute : Attribute
{
}
