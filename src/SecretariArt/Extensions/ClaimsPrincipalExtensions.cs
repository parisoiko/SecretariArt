using System.Security.Claims;

namespace SecretariArt.Extensions;

public static class ClaimsPrincipalExtensions
{
    // The signed-in person's id. Every page but sign-in and registration requires someone signed in (the fallback
    // policy), so a missing id is a bug, not a visitor.
    public static string UserId(this ClaimsPrincipal user) =>
        user.FindFirstValue(ClaimTypes.NameIdentifier) ?? throw new InvalidOperationException("No one is signed in.");
}
