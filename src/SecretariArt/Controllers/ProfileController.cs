using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using SecretariArt.Models;
using SecretariArt.ViewModels;

namespace SecretariArt.Controllers;

// "My account": the signed-in person's own details and password. Separate from AccountController, whose pages are
// for visitors ([AllowAnonymous] on a controller would also open these).
[Route("account/profile")]
public class ProfileController(UserManager<ApplicationUser> users, SignInManager<ApplicationUser> signIn) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        ApplicationUser user = await CurrentUserAsync();
        return View(new AccountPageViewModel
        {
            Email = user.Email!,
            Profile = new ProfileViewModel { FirstName = user.FirstName, LastName = user.LastName, PhoneNumber = user.PhoneNumber ?? "" },
        });
    }

    [HttpPost("")]
    public async Task<IActionResult> Index([Bind(Prefix = nameof(AccountPageViewModel.Profile))] ProfileViewModel profile)
    {
        ApplicationUser user = await CurrentUserAsync();
        if (!ModelState.IsValid)
        {
            return View(new AccountPageViewModel { Email = user.Email!, Profile = profile });
        }

        user.FirstName = profile.FirstName.Trim();
        user.LastName = profile.LastName.Trim();
        user.PhoneNumber = profile.PhoneNumber.Trim();
        await users.UpdateAsync(user);
        // The name is in the sign-in cookie (AppClaimsPrincipalFactory): issue a new one so the menu shows it now.
        await signIn.RefreshSignInAsync(user);

        TempData["Message"] = "Your details have been saved.";
        return RedirectToAction(nameof(Index));
    }

    // After a refused change the browser shows this address; opening it again goes back to the page (QA, A2).
    [HttpGet("password")]
    public IActionResult ChangePassword() => RedirectToAction(nameof(Index));

    [HttpPost("password")]
    public async Task<IActionResult> ChangePassword([Bind(Prefix = nameof(AccountPageViewModel.Password))] ChangePasswordViewModel password)
    {
        ApplicationUser user = await CurrentUserAsync();
        AccountPageViewModel page() => new()
        {
            Email = user.Email!,
            Profile = new ProfileViewModel { FirstName = user.FirstName, LastName = user.LastName, PhoneNumber = user.PhoneNumber ?? "" },
            Password = password,
        };
        if (!ModelState.IsValid)
        {
            return View(nameof(Index), page());
        }

        IdentityResult result = await users.ChangePasswordAsync(user, password.CurrentPassword, password.NewPassword);
        if (!result.Succeeded)
        {
            foreach (IdentityError error in result.Errors)
            {
                string field = error.Code == "PasswordMismatch"
                    ? nameof(ChangePasswordViewModel.CurrentPassword)
                    : nameof(ChangePasswordViewModel.NewPassword);
                ModelState.AddModelError($"{nameof(AccountPageViewModel.Password)}.{field}",
                    error.Code == "PasswordMismatch" ? "That isn't your current password." : error.Description);
            }
            return View(nameof(Index), page());
        }

        // Changing the password changes the security stamp, which signs out every other device within the
        // validation interval; this one gets a fresh cookie and stays signed in.
        await signIn.RefreshSignInAsync(user);
        TempData["Message"] = "Your password has been changed.";
        return RedirectToAction(nameof(Index));
    }

    // The fallback policy guarantees someone is signed in; their account can only be missing if it was just deleted.
    private async Task<ApplicationUser> CurrentUserAsync() =>
        await users.GetUserAsync(User) ?? throw new InvalidOperationException("The signed-in user no longer exists.");
}
