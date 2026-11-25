using Microsoft.AspNetCore.Mvc;
using SecretariArt.Models;
using SecretariArt.Services;
using SecretariArt.ViewModels;

namespace SecretariArt.Controllers;

[Route("admin/{organizationId:int}/classrooms")]
public class ClassroomsController(IOrganizationService organizations, IClassroomService classrooms)
    : OrganizationAdminController(organizations)
{
    [HttpGet("")]
    public async Task<IActionResult> Index() => View(await classrooms.GetAllAsync(OrganizationId));

    [HttpGet("new")]
    public IActionResult Create() => View("Edit", new ClassroomViewModel());

    [HttpPost("new")]
    public Task<IActionResult> Create(ClassroomViewModel model) =>
        SaveAsync(model, () => classrooms.CreateAsync(OrganizationId, model.ToInput()));

    [HttpGet("{classroomId:int}/edit")]
    public async Task<IActionResult> Edit(int classroomId)
    {
        Classroom? classroom = await classrooms.FindAsync(OrganizationId, classroomId);
        return classroom == null ? NotFound() : View(ClassroomViewModel.From(classroom));
    }

    [HttpPost("{classroomId:int}/edit")]
    public Task<IActionResult> Edit(int classroomId, ClassroomViewModel model)
    {
        model.Id = classroomId;
        return SaveAsync(model, () => classrooms.UpdateAsync(OrganizationId, classroomId, model.ToInput()));
    }

    [HttpPost("{classroomId:int}/delete")]
    public async Task<IActionResult> Delete(int classroomId) =>
        ReportAndRedirect(await classrooms.DeleteAsync(OrganizationId, classroomId), nameof(Index));

    // A refusal (e.g. a duplicate name) is shown on the form itself, with what was typed kept.
    private async Task<IActionResult> SaveAsync(ClassroomViewModel model, Func<Task<ServiceResult>> save)
    {
        if (!ModelState.IsValid)
        {
            return View("Edit", model);
        }

        ServiceResult result = await save();
        if (!result.Succeeded)
        {
            ModelState.AddModelError("", result.Error!);
            return View("Edit", model);
        }
        return ReportAndRedirect(result, nameof(Index));
    }
}
