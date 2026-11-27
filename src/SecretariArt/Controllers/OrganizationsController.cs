using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SecretariArt.Extensions;
using SecretariArt.Models;
using SecretariArt.Services;
using SecretariArt.ViewModels;

namespace SecretariArt.Controllers;

// Asking for a new organisation, and joining an existing one with its invite code.
public class OrganizationsController(IOrganizationService organizations, IMembershipService memberships) : Controller
{
    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    [HttpGet("organizations/request")]
    public IActionResult RequestOrganization() => View(new OrganizationRequestViewModel());

    [EnableRateLimiting(RateLimitPolicies.OrganizationRequests)]
    [HttpPost("organizations/request")]
    public async Task<IActionResult> RequestOrganization(OrganizationRequestViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        ServiceResult result = await organizations.RequestAsync(UserId, new OrganizationRequest(
            model.Name, model.Type!.Value, model.City, model.ContactEmail, model.ContactPhone));
        if (!result.Succeeded)
        {
            ModelState.AddModelError(string.Empty, result.Error!);
            return View(model);
        }

        TempData["Message"] = $"Thanks! We've received your request for {model.Name.Trim()} and will review it shortly.";
        return RedirectToAction("Index", "Home");
    }

    // The Home page's invite-code box sends the code along, so only the role is left to choose.
    [HttpGet("join")]
    public IActionResult Join(string? code = null) =>
        View(new JoinViewModel { InviteCode = string.IsNullOrWhiteSpace(code) ? "" : InviteCodeGenerator.Normalize(code) });

    // The invite link admins share: /join/ATHENS26. Anonymous visitors are sent to register first and come back here.
    [AllowAnonymous]
    [EnableRateLimiting(RateLimitPolicies.InviteCodes)]
    [HttpGet("join/{code}")]
    public async Task<IActionResult> JoinWithCode(string code)
    {
        if (User.Identity?.IsAuthenticated != true)
        {
            return RedirectToAction("Register", "Account", new { returnUrl = Url.Action(nameof(JoinWithCode), new { code }) });
        }

        Organization? organization = await memberships.FindByInviteCodeAsync(code);
        var model = new JoinViewModel { InviteCode = InviteCodeGenerator.Normalize(code), OrganizationName = organization?.Name };
        if (organization == null)
        {
            ModelState.AddModelError(nameof(JoinViewModel.InviteCode),
                "This invite link isn't valid any more. Ask the organisation for a new one.");
        }

        return View(nameof(Join), model);
    }

    [EnableRateLimiting(RateLimitPolicies.InviteCodes)]
    [HttpPost("join")]
    public async Task<IActionResult> Join(JoinViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        ServiceResult result = await memberships.JoinAsync(UserId, model.InviteCode, model.Role!.Value);
        if (!result.Succeeded)
        {
            ModelState.AddModelError(string.Empty, result.Error!);
            return View(model);
        }

        Organization organization = (await memberships.FindByInviteCodeAsync(model.InviteCode))!;
        TempData["Message"] = $"Request sent. {organization.Name} will confirm your membership.";
        return RedirectToAction("Index", "Home");
    }
}
