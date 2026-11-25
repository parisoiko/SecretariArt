using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SecretariArt.Authorization;
using SecretariArt.Models;
using SecretariArt.Services;
using SecretariArt.ViewModels;

namespace SecretariArt.Controllers;

[Route("admin")]
public class AdministrationController(
    ICurrentUserMemberships currentMemberships,
    IOrganizationService organizations,
    IMembershipService memberships) : Controller
{
    // Administration of a single organisation goes straight there; several get a chooser.
    [HttpGet("")]
    public async Task<IActionResult> Index()
    {
        var administered = (await currentMemberships.GetAsync())
            .Where(m => m.Role == MembershipRole.Admin && m.Status == MembershipStatus.Active &&
                        m.Organization!.Status == OrganizationStatus.Active)
            .Select(m => m.Organization!)
            .ToList();

        return administered.Count switch
        {
            0 => Forbid(),
            1 => RedirectToAction(nameof(Members), new { organizationId = administered[0].Id }),
            _ => View(administered),
        };
    }

    [Authorize(Policy = Policies.OrganizationAdmin)]
    [HttpGet("{organizationId:int}/members")]
    public async Task<IActionResult> Members(int organizationId)
    {
        Organization organization = (await organizations.FindAsync(organizationId))!;
        List<Membership> all = await memberships.GetForOrganizationAsync(organizationId);

        return View(new MembersViewModel
        {
            Organization = organization,
            Pending = all.Where(m => m.Status == MembershipStatus.Pending).OrderBy(m => m.RequestedAt).ToList(),
            Active = all.Where(m => m.Status == MembershipStatus.Active).ToList(),
            InviteLink = Url.Action("JoinWithCode", "Organizations", new { code = organization.InviteCode },
                Request.Scheme)!,
        });
    }

    [Authorize(Policy = Policies.OrganizationAdmin)]
    [HttpPost("{organizationId:int}/members/{membershipId:int}/approve")]
    public Task<IActionResult> Approve(int organizationId, int membershipId) => Decide(organizationId, membershipId, approve: true);

    [Authorize(Policy = Policies.OrganizationAdmin)]
    [HttpPost("{organizationId:int}/members/{membershipId:int}/reject")]
    public Task<IActionResult> Reject(int organizationId, int membershipId) => Decide(organizationId, membershipId, approve: false);

    [Authorize(Policy = Policies.OrganizationAdmin)]
    [HttpPost("{organizationId:int}/invite-code")]
    public async Task<IActionResult> RegenerateInviteCode(int organizationId)
    {
        string code = await organizations.RegenerateInviteCodeAsync(organizationId);
        TempData["Message"] = $"New invite code: {code}. The old code and link no longer work.";
        return RedirectToAction(nameof(Members), new { organizationId });
    }

    private async Task<IActionResult> Decide(int organizationId, int membershipId, bool approve)
    {
        ServiceResult result = await memberships.DecideAsync(organizationId, membershipId, approve);
        TempData["Message"] = result.Succeeded ? (approve ? "Request approved." : "Request rejected.") : result.Error;
        return RedirectToAction(nameof(Members), new { organizationId });
    }
}
