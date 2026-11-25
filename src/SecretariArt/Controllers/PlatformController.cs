using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SecretariArt.Authorization;
using SecretariArt.Extensions;
using SecretariArt.Models;
using SecretariArt.Services;
using SecretariArt.ViewModels;

namespace SecretariArt.Controllers;

// For the people running SecretariArt itself: decide on requests for new organisations, and see every organisation.
[Authorize(Roles = Policies.PlatformAdminRole)]
[Route("platform")]
public class PlatformController(IOrganizationService organizations) : Controller
{
    [HttpGet("organizations")]
    public async Task<IActionResult> Organizations() => View(await organizations.GetPendingAsync());

    [HttpGet("organizations/all")]
    public async Task<IActionResult> AllOrganizations(OrganizationStatus? status) =>
        View(new AllOrganizationsViewModel { Organizations = await organizations.GetAllAsync(status), Status = status });

    [HttpPost("organizations/{id:int}/approve")]
    public async Task<IActionResult> Approve(int id)
    {
        this.Report(await organizations.ApproveAsync(id), "Organisation approved. Its first admin can now set it up.");
        return RedirectToAction(nameof(Organizations));
    }

    // Rejecting has its own page: the platform admin must give a reason, which the requester then sees.
    [HttpGet("organizations/{id:int}/reject")]
    public async Task<IActionResult> Reject(int id)
    {
        Organization? request = await organizations.FindPendingRequestAsync(id);
        return request == null ? RequestGone() : View("Reject", Describe(request, new RejectViewModel()));
    }

    [HttpPost("organizations/{id:int}/reject")]
    public async Task<IActionResult> Reject(int id, RejectViewModel model)
    {
        if (!ModelState.IsValid)
        {
            Organization? request = await organizations.FindPendingRequestAsync(id);
            return request == null ? RequestGone() : View("Reject", Describe(request, model));
        }

        this.Report(await organizations.RejectAsync(id, model.Reason));
        return RedirectToAction(nameof(Organizations));
    }

    private RejectViewModel Describe(Organization request, RejectViewModel model)
    {
        model.Heading = $"Reject the request for {request.Name}";
        model.Details = $"{request.Type.Display()} in {request.City}, requested by {request.RequestedBy!.FullName} " +
                        $"({request.RequestedBy.Email}). They'll see your reason on their Home page.";
        model.CancelUrl = Url.Action(nameof(Organizations))!;
        return model;
    }

    private IActionResult RequestGone()
    {
        this.Report(ServiceResult.Fail("We couldn't find that request, or it has already been handled."));
        return RedirectToAction(nameof(Organizations));
    }
}
