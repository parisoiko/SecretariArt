using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using SecretariArt.Authorization;
using SecretariArt.Extensions;
using SecretariArt.Models;
using SecretariArt.Services;
using SecretariArt.ViewModels;

namespace SecretariArt.Controllers;

[Route("admin")]
public class AdministrationController(
    ICurrentUserMemberships currentMemberships,
    IOrganizationService organizations,
    IMembershipService memberships,
    IRulesService rules,
    IClassroomService classrooms,
    ICourseService courses) : Controller
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
        List<CourseOverview> courseList = await courses.GetAllAsync(organizationId);
        ViewData["Organization"] = organization; // for the shared header and tabs

        return View(new MembersViewModel
        {
            Organization = organization,
            Pending = all.Where(m => m.Status == MembershipStatus.Pending).OrderBy(m => m.RequestedAt).ToList(),
            Active = all.Where(m => m.Status == MembershipStatus.Active).ToList(),
            Rejected = all.Where(m => m.Status == MembershipStatus.Rejected)
                .OrderByDescending(m => m.DecidedAt).Take(MembersViewModel.RejectedShown).ToList(),
            InviteLink = Url.Action("JoinWithCode", "Organizations", new { code = organization.InviteCode },
                Request.Scheme)!,
            Setup = new SetupProgress(
                RulesSaved: await rules.HasSavedAsync(organizationId),
                HasClassrooms: (await classrooms.GetAllAsync(organizationId)).Count > 0,
                HasCourseWithGroup: courseList.Any(c => c.Course.Groups.Count > 0),
                HasStudents: all.Any(m => m.Role == MembershipRole.Student && m.Status == MembershipStatus.Active),
                HasEnrolments: courseList.Any(c => c.Enrolled > 0)),
        });
    }

    [Authorize(Policy = Policies.OrganizationAdmin)]
    [HttpPost("{organizationId:int}/members/{membershipId:int}/approve")]
    public async Task<IActionResult> Approve(int organizationId, int membershipId, string? returnUrl = null)
    {
        this.Report(await memberships.ApproveAsync(organizationId, membershipId));
        // Approving from the Home page ("Needs your attention") goes back there.
        return Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl!) : RedirectToAction(nameof(Members), new { organizationId });
    }

    // Rejecting has its own page: the admin must give a reason, which the person then sees on their Home page.
    [Authorize(Policy = Policies.OrganizationAdmin)]
    [HttpGet("{organizationId:int}/members/{membershipId:int}/reject")]
    public async Task<IActionResult> Reject(int organizationId, int membershipId)
    {
        Membership? request = await memberships.FindPendingAsync(organizationId, membershipId);
        return request == null ? RequestGone(organizationId) : View("Reject", Describe(request, new RejectViewModel()));
    }

    [Authorize(Policy = Policies.OrganizationAdmin)]
    [HttpPost("{organizationId:int}/members/{membershipId:int}/reject")]
    public async Task<IActionResult> Reject(int organizationId, int membershipId, RejectViewModel model)
    {
        if (!ModelState.IsValid)
        {
            Membership? request = await memberships.FindPendingAsync(organizationId, membershipId);
            return request == null ? RequestGone(organizationId) : View("Reject", Describe(request, model));
        }

        this.Report(await memberships.RejectAsync(organizationId, membershipId, model.Reason));
        return RedirectToAction(nameof(Members), new { organizationId });
    }

    [Authorize(Policy = Policies.OrganizationAdmin)]
    [HttpPost("{organizationId:int}/invite-code")]
    public async Task<IActionResult> RegenerateInviteCode(int organizationId)
    {
        string code = await organizations.RegenerateInviteCodeAsync(organizationId);
        TempData["Message"] = $"New invite code: {code}. The old code and link no longer work.";
        return RedirectToAction(nameof(Members), new { organizationId });
    }

    private RejectViewModel Describe(Membership request, RejectViewModel model)
    {
        string role = request.Role.ToString().ToLowerInvariant();
        model.Heading = $"Reject {request.User!.FullName}'s request";
        model.Details = $"{request.User.FullName} ({request.User.Email}) asked to join as a {role}. " +
                        $"They'll see your reason and can ask again after {RejectionReasons.RejoinWait.TotalMinutes:0} minutes.";
        model.CancelUrl = Url.Action(nameof(Members), new { organizationId = request.OrganizationId })!;
        return model;
    }

    private IActionResult RequestGone(int organizationId)
    {
        this.Report(ServiceResult.Fail("We couldn't find that request, or it has already been handled."));
        return RedirectToAction(nameof(Members), new { organizationId });
    }
}
