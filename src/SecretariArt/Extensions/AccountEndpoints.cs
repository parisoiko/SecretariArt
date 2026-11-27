using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Identity;
using SecretariArt.Models;

namespace SecretariArt.Extensions;

public static class AccountEndpoints
{
    // Sign-out has no page: the account menu posts here and the person lands on the sign-in page. A plain endpoint
    // (a "minimal API") rather than a component, since there's nothing to render. Replaces AccountController.Logout.
    public static void MapAccountEndpoints(this WebApplication app) =>
        app.MapPost("/account/logout", async (HttpContext context, SignInManager<ApplicationUser> signIn) =>
            {
                // UseAntiforgery checks the token of any endpoint marked as needing one, but only Blazor pages and form
                // binding act on the result by themselves: here, a bad or missing token must be refused by hand.
                if (context.Features.Get<IAntiforgeryValidationFeature>() is { IsValid: false })
                {
                    return Results.BadRequest();
                }
                await signIn.SignOutAsync();
                return Results.LocalRedirect("/account/login");
            })
            .WithMetadata(new RequireAntiforgeryTokenAttribute())
            // Signing out someone who isn't signed in does nothing, as before.
            .AllowAnonymous();
}
