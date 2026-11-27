using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using SecretariArt.Extensions;
using SecretariArt.Models;
using SecretariArt.Services;
using SecretariArt.ViewModels;

namespace SecretariArt.Controllers;

// The timetable generator. The admin chooses what to keep, the generator (OR-Tools CP-SAT) works out a
// draft, and the admin looks it over before anything changes: use it, or discard it.
[Route("admin/{organizationId:int}/schedule")]
public class TimetableDraftController(IOrganizationService organizations, ITimetableDraftService drafts, IScheduleService schedule)
    : OrganizationAdminController(organizations)
{
    [HttpGet("generate")]
    public async Task<IActionResult> Generate()
    {
        OrganizationSchedule current = await schedule.GetAsync(OrganizationId);
        return View(new GenerateViewModel
        {
            Schedule = current,
            HasDraft = await drafts.ExistsAsync(OrganizationId),
            // Filling the gaps is the gentle choice once some classes are placed; with none, there's nothing to keep.
            Mode = current.Sessions.Count > 0 ? GenerationMode.FillGaps : GenerationMode.StartOver,
        });
    }

    // Seconds of CPU per run: limited like the other actions someone could script (Extensions/RateLimiting.cs).
    [HttpPost("generate")]
    [EnableRateLimiting(RateLimitPolicies.Generation)]
    public async Task<IActionResult> Generate(GenerationMode mode)
    {
        ServiceResult result = await drafts.GenerateAsync(OrganizationId, mode, UserId, HttpContext.RequestAborted);
        return ReportAndRedirect(result, result.Succeeded ? nameof(Draft) : nameof(Generate));
    }

    [HttpGet("draft")]
    public async Task<IActionResult> Draft()
    {
        DraftView? draft = await drafts.GetAsync(OrganizationId);
        if (draft == null)
        {
            this.Report(ServiceResult.Fail("There's no draft at the moment: generate one."));
            return RedirectToAction(nameof(Generate), OrganizationRoute());
        }
        return View(draft);
    }

    [HttpPost("draft/use")]
    public async Task<IActionResult> Use()
    {
        ServiceResult result = await drafts.UseAsync(OrganizationId);
        if (result.Succeeded)
        {
            return ReportAndRedirect(result, nameof(ScheduleController.Index), new { controller = "Schedule" });
        }
        // A draft that no longer fits: its page lists why already, so no banner saying it again (QA, A4).
        if ((await drafts.GetAsync(OrganizationId))?.OutOfDate.Count > 0)
        {
            return RedirectToAction(nameof(Draft), OrganizationRoute());
        }
        return ReportAndRedirect(result, nameof(Draft));
    }

    [HttpPost("draft/discard")]
    public async Task<IActionResult> Discard() =>
        ReportAndRedirect(await drafts.DiscardAsync(OrganizationId), nameof(ScheduleController.Index), new { controller = "Schedule" });
}
