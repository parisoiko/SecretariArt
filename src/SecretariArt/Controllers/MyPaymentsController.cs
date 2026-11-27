using Microsoft.AspNetCore.Mvc;
using SecretariArt.Extensions;
using SecretariArt.Services;
using SecretariArt.ViewModels;

namespace SecretariArt.Controllers;

// A student's own payments: what they owe and have paid, in every organisation. It is personal, so someone without
// the student role (e.g. an admin-only account) is sent Home, like the navigation.
[Route("payments")]
public class MyPaymentsController(ICurrentUserMemberships currentMemberships, IBillingService billing, TimeProvider clock) : Controller
{
    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        UserRoles roles = await currentMemberships.GetRolesAsync();
        if (!roles.IsStudent)
        {
            return RedirectToAction("Index", "Home");
        }
        return View(new MyPaymentsViewModel
        {
            Statements = await billing.GetMineAsync(User.UserId()), Today = clock.SchoolToday(),
        });
    }
}
