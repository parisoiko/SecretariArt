using Microsoft.AspNetCore.Mvc;
using SecretariArt.Models;
using SecretariArt.Services;
using SecretariArt.ViewModels;

namespace SecretariArt.Controllers;

[Route("admin/{organizationId:int}/rules")]
public class RulesController(IOrganizationService organizations, IRulesService rules, IScheduleService schedule)
    : OrganizationAdminController(organizations)
{
    [HttpGet("")]
    public async Task<IActionResult> Edit() =>
        View(RulesViewModel.From(await rules.GetAsync(OrganizationId), await rules.HasSavedAsync(OrganizationId)));

    [HttpPost("")]
    public async Task<IActionResult> Edit(RulesViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return View(model);
        }

        // Tightening the rules can flag classes already in the timetable: count them, so the admin hears about it
        // here rather than finding out on the Schedule tab (QA, S2).
        IReadOnlyDictionary<int, int> before = await schedule.FlagsAsync(OrganizationId);
        ServiceResult result = await rules.SaveAsync(OrganizationId, model.ToInput());
        if (!result.Succeeded)
        {
            ModelState.AddModelError("", result.Error!);
            return View(model);
        }

        IReadOnlyDictionary<int, int> after = await schedule.FlagsAsync(OrganizationId);
        int flagged = after.Count(s => s.Value > before.GetValueOrDefault(s.Key));
        if (flagged > 0)
        {
            result = ServiceResult.Ok($"{result.Message} {DisplayNames.Plural(flagged, "class", "classes")} in the timetable " +
                                      $"{(flagged == 1 ? "has" : "have")} new problems or notes because of the change: " +
                                      "see Needs attention on the Schedule tab.");
        }
        return ReportAndRedirect(result, nameof(Edit));
    }
}
