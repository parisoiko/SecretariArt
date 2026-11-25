using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SecretariArt.Authorization;
using SecretariArt.Services;

namespace SecretariArt.Controllers;

// For the people running SecretariArt itself: approve or reject requests for new organisations.
[Authorize(Roles = Policies.PlatformAdminRole)]
[Route("platform")]
public class PlatformController(IOrganizationService organizations) : Controller
{
    [HttpGet("organizations")]
    public async Task<IActionResult> Organizations() => View(await organizations.GetPendingAsync());

    [HttpPost("organizations/{id:int}/approve")]
    public async Task<IActionResult> Approve(int id)
    {
        ServiceResult result = await organizations.ApproveAsync(id);
        TempData["Message"] = result.Succeeded ? "Organisation approved. Its first admin can now set it up." : result.Error;
        return RedirectToAction(nameof(Organizations));
    }

    [HttpPost("organizations/{id:int}/reject")]
    public async Task<IActionResult> Reject(int id)
    {
        ServiceResult result = await organizations.RejectAsync(id);
        TempData["Message"] = result.Succeeded ? "Request rejected." : result.Error;
        return RedirectToAction(nameof(Organizations));
    }
}
