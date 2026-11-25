using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SecretariArt.Services;

namespace SecretariArt.Controllers;

// Schedule and Payments: the sections exist (so navigation and roles are in place); their content comes in later milestones.
// Both are personal, so someone without the matching role (e.g. an admin-only account) is sent Home, like the navigation.
public class SectionsController(ICurrentUserMemberships currentMemberships) : Controller
{
    [HttpGet("schedule")]
    public async Task<IActionResult> Schedule()
    {
        UserRoles roles = await currentMemberships.GetRolesAsync();
        return roles.IsTeacher || roles.IsStudent ? View(roles) : RedirectToAction("Index", "Home");
    }

    [HttpGet("payments")]
    public async Task<IActionResult> Payments()
    {
        UserRoles roles = await currentMemberships.GetRolesAsync();
        return roles.IsStudent ? View(roles) : RedirectToAction("Index", "Home");
    }
}

[AllowAnonymous]
public class ErrorController : Controller
{
    [Route("error/{statusCode:int}")]
    public IActionResult Show(int statusCode)
    {
        Response.StatusCode = statusCode;
        return View(statusCode);
    }
}
