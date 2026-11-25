using Microsoft.AspNetCore.Mvc;
using SecretariArt.Models;
using SecretariArt.Services;
using SecretariArt.ViewModels;

namespace SecretariArt.Controllers;

[Route("admin/{organizationId:int}/bundles")]
public class BundlesController(IOrganizationService organizations, ICourseService courses)
    : OrganizationAdminController(organizations)
{
    [HttpGet("")]
    public async Task<IActionResult> Index() => View(await courses.GetBundlesAsync(OrganizationId));

    [HttpGet("new")]
    public async Task<IActionResult> Create() => View("Edit", await PrepareAsync(new BundleViewModel()));

    [HttpPost("new")]
    public Task<IActionResult> Create(BundleViewModel model) =>
        SaveAsync(model, () => courses.CreateBundleAsync(OrganizationId, model.ToInput()));

    [HttpGet("{bundleId:int}/edit")]
    public async Task<IActionResult> Edit(int bundleId)
    {
        Bundle? bundle = await courses.FindBundleAsync(OrganizationId, bundleId);
        if (bundle == null)
        {
            return NotFound();
        }

        return View(await PrepareAsync(new BundleViewModel
        {
            Id = bundle.Id, Name = bundle.Name, Description = bundle.Description,
            CourseIds = bundle.Courses.Select(l => l.CourseId).ToList(),
        }));
    }

    [HttpPost("{bundleId:int}/edit")]
    public Task<IActionResult> Edit(int bundleId, BundleViewModel model)
    {
        model.Id = bundleId;
        return SaveAsync(model, () => courses.UpdateBundleAsync(OrganizationId, bundleId, model.ToInput()));
    }

    [HttpPost("{bundleId:int}/delete")]
    public async Task<IActionResult> Delete(int bundleId) =>
        ReportAndRedirect(await courses.DeleteBundleAsync(OrganizationId, bundleId), nameof(Index));

    private async Task<IActionResult> SaveAsync(BundleViewModel model, Func<Task<ServiceResult>> save)
    {
        if (!ModelState.IsValid)
        {
            return View("Edit", await PrepareAsync(model));
        }

        ServiceResult result = await save();
        if (!result.Succeeded)
        {
            ModelState.AddModelError("", result.Error!);
            return View("Edit", await PrepareAsync(model));
        }
        return ReportAndRedirect(result, nameof(Index));
    }

    private async Task<BundleViewModel> PrepareAsync(BundleViewModel model)
    {
        model.AvailableCourses = (await courses.GetAllAsync(OrganizationId)).Select(o => o.Course).ToList();
        return model;
    }
}
