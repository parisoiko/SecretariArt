using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.Extensions.Options;
using SecretariArt.Extensions;
using SecretariArt.Models;
using SecretariArt.Services;
using SecretariArt.ViewModels;

namespace SecretariArt.Controllers;

[AllowAnonymous]
[Route("account")]
public class AccountController(UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signIn) : Controller
{
    [HttpGet("register")]
    public IActionResult Register(string? returnUrl = null)
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return RedirectToLocal(returnUrl);
        }

        ViewData["ReturnUrl"] = returnUrl;
        return View(new RegisterViewModel());
    }

    [EnableRateLimiting(RateLimitPolicies.Register)]
    [HttpPost("register")]
    public async Task<IActionResult> Register(RegisterViewModel model, string? returnUrl = null)
    {
        ViewData["ReturnUrl"] = returnUrl;
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        var user = new ApplicationUser
        {
            UserName = model.Email.Trim(),
            Email = model.Email.Trim(),
            FirstName = model.FirstName.Trim(),
            LastName = model.LastName.Trim(),
            PhoneNumber = model.PhoneNumber.Trim(),
        };

        IdentityResult result = await users.CreateAsync(user, model.Password);
        if (!result.Succeeded)
        {
            // The user name is the email, so both duplicate errors mean the same thing: report it once, plainly,
            // with a way to sign in. (This does tell people the email has an account; hiding that would need
            // confirmation emails, which the app doesn't send yet. A deliberate choice, see the QA report, F8.)
            if (result.Errors.Any(e => e.Code is "DuplicateEmail" or "DuplicateUserName"))
            {
                ModelState.AddModelError(nameof(model.Email), "There's already an account with this email.");
                ViewData["EmailTaken"] = true;
            }

            foreach (IdentityError error in result.Errors.Where(e => e.Code is not ("DuplicateEmail" or "DuplicateUserName")))
            {
                string field = error.Code switch
                {
                    "InvalidEmail" => nameof(model.Email),
                    _ when error.Code.StartsWith("Password") => nameof(model.Password),
                    _ => string.Empty,
                };
                ModelState.AddModelError(field, error.Description);
            }

            return View(model);
        }

        await signIn.SignInAsync(user, isPersistent: false);
        return RedirectToLocal(returnUrl);
    }

    [HttpGet("login")]
    public IActionResult Login(string? returnUrl = null, string? email = null)
    {
        if (User.Identity?.IsAuthenticated == true)
        {
            return RedirectToLocal(returnUrl);
        }

        ViewData["ReturnUrl"] = returnUrl;
        // Pre-filled when arriving from "Sign in instead" on the registration form.
        return View(new LoginViewModel { Email = email ?? "" });
    }

    [EnableRateLimiting(RateLimitPolicies.SignIn)]
    [HttpPost("login")]
    public async Task<IActionResult> Login(LoginViewModel model, string? returnUrl = null)
    {
        ViewData["ReturnUrl"] = returnUrl;
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        // The user name is the email address.
        var result = await signIn.PasswordSignInAsync(model.Email.Trim(), model.Password, model.RememberMe, lockoutOnFailure: true);
        if (result.Succeeded)
        {
            return RedirectToLocal(returnUrl);
        }

        // One message for unknown email and wrong password, so the form doesn't reveal who has an account.
        ModelState.AddModelError(string.Empty, result.IsLockedOut
            ? "Too many failed attempts. Try again in a few minutes."
            : "Wrong email or password.");
        return View(model);
    }

    // Anonymous like the rest of the controller: signing out someone who isn't signed in does nothing.
    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        await signIn.SignOutAsync();
        return RedirectToAction(nameof(Login));
    }

    [HttpGet("reset-password")]
    public IActionResult ResetPassword(string? email = null, string? code = null) =>
        string.IsNullOrEmpty(email) || string.IsNullOrEmpty(code)
            ? Redirect("/account/forgot-password")
            : View(new ResetPasswordViewModel { Email = email, Code = code });

    [EnableRateLimiting(RateLimitPolicies.AccountRecovery)]
    [HttpPost("reset-password")]
    public async Task<IActionResult> ResetPassword(ResetPasswordViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        // An unknown email gets the same answer as a bad link.
        ApplicationUser? user = await users.FindByEmailAsync(model.Email);
        IdentityResult result = user is null
            ? IdentityResult.Failed(new IdentityError { Code = "InvalidToken" })
            : await users.ResetPasswordAsync(user, PasswordResetService.DecodeCode(model.Code), model.Password);
        if (result.Succeeded)
        {
            // Whoever was locked out by wrong guesses has now proved they own the email.
            await users.SetLockoutEndDateAsync(user!, null);
            await users.ResetAccessFailedCountAsync(user!);
            TempData["Message"] = "Your password has been changed. Sign in with the new one.";
            return RedirectToAction(nameof(Login), new { email = model.Email });
        }

        foreach (IdentityError error in result.Errors)
        {
            bool badLink = error.Code == "InvalidToken";
            ModelState.AddModelError(badLink ? string.Empty : nameof(model.Password), badLink
                ? "This reset link has expired or has already been used. Ask for a new one."
                : error.Description);
            ViewData["LinkExpired"] = badLink;
        }
        return View(model);
    }

    // Only ever redirect inside the site: returnUrl comes from the query string and could point anywhere.
    private IActionResult RedirectToLocal(string? returnUrl) =>
        Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : RedirectToAction("Index", "Home");
}
