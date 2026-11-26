using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SecretariArt.Services;

namespace SecretariArt.Controllers;

// Payments: the section exists (so navigation and roles are in place); its content comes in milestone 4.
// It is personal, so someone without the student role (e.g. an admin-only account) is sent Home, like the navigation.
public class SectionsController(ICurrentUserMemberships currentMemberships) : Controller
{
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
