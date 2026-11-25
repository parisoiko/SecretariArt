using Microsoft.AspNetCore.Mvc;
using SecretariArt.Services;
using SecretariArt.ViewModels;

namespace SecretariArt.Controllers;

[Route("admin/{organizationId:int}/rules")]
public class RulesController(IOrganizationService organizations, IRulesService rules) : OrganizationAdminController(organizations)
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

        ServiceResult result = await rules.SaveAsync(OrganizationId, model.ToInput());
        if (!result.Succeeded)
        {
            ModelState.AddModelError("", result.Error!);
            return View(model);
        }
        return ReportAndRedirect(result, nameof(Edit));
    }
}
