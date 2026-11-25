using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SecretariArt.Services;

namespace SecretariArt.Controllers;

// Schedule and Payments: the sections exist (so navigation and roles are in place); their content comes in later milestones.
public class SectionsController(ICurrentUserMemberships currentMemberships) : Controller
{
    [HttpGet("schedule")]
    public async Task<IActionResult> Schedule() => View(await currentMemberships.GetRolesAsync());

    [HttpGet("payments")]
    public async Task<IActionResult> Payments() => View(await currentMemberships.GetRolesAsync());
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
