using Microsoft.AspNetCore.Mvc;
using SecretariArt.Models;
using SecretariArt.Services;
using SecretariArt.ViewModels;

namespace SecretariArt.Controllers;

[Route("admin/{organizationId:int}/courses")]
public class CoursesController(
    IOrganizationService organizations,
    ICourseService courses,
    IRulesService rules,
    IMembershipService memberships,
    IEnrolmentService enrolments) : OrganizationAdminController(organizations)
{
    [HttpGet("")]
    public async Task<IActionResult> Index() => View(await courses.GetAllAsync(OrganizationId));

    [HttpGet("{courseId:int}")]
    public async Task<IActionResult> Details(int courseId)
    {
        CourseOverview? overview = await courses.GetAsync(OrganizationId, courseId);
        if (overview == null)
        {
            return NotFound();
        }

        var taking = overview.Course.Groups.SelectMany(g => g.Enrolments).Select(e => e.StudentId).ToHashSet();
        var students = await memberships.GetActivePeopleAsync(OrganizationId, MembershipRole.Student);
        return View(new CourseDetailsViewModel
        {
            Overview = overview,
            StudentsNotTaking = students.Where(s => !taking.Contains(s.Id)).ToList(),
        });
    }

    [HttpGet("new")]
    public async Task<IActionResult> Create() => View("Edit", await PrepareAsync(new CourseViewModel()));

    [HttpPost("new")]
    public async Task<IActionResult> Create(CourseViewModel model)
    {
        if (!ModelState.IsValid)
        {
            return View("Edit", await PrepareAsync(model));
        }

        ServiceResult result = await courses.CreateAsync(OrganizationId, model.ToInput());
        if (!result.Succeeded)
        {
            ModelState.AddModelError("", result.Error!);
            return View("Edit", await PrepareAsync(model));
        }

        // Straight to the new course, where its first group is added.
        return ReportAndRedirect(result, nameof(Details), new { courseId = result.CreatedId });
    }

    [HttpGet("{courseId:int}/edit")]
    public async Task<IActionResult> Edit(int courseId)
    {
        Course? course = await courses.FindAsync(OrganizationId, courseId);
        return course == null ? NotFound() : View(await PrepareAsync(CourseViewModel.From(course)));
    }

    [HttpPost("{courseId:int}/edit")]
    public async Task<IActionResult> Edit(int courseId, CourseViewModel model)
    {
        model.Id = courseId;
        if (!ModelState.IsValid)
        {
            return View(await PrepareAsync(model));
        }

        ServiceResult result = await courses.UpdateAsync(OrganizationId, courseId, model.ToInput());
        if (!result.Succeeded)
        {
            ModelState.AddModelError("", result.Error!);
            return View(await PrepareAsync(model));
        }
        return ReportAndRedirect(result, nameof(Details), new { courseId });
    }

    [HttpPost("{courseId:int}/delete")]
    public async Task<IActionResult> Delete(int courseId) =>
        ReportAndRedirect(await courses.DeleteAsync(OrganizationId, courseId), nameof(Index));

    // ---- Groups ----

    [HttpGet("{courseId:int}/groups/new")]
    public async Task<IActionResult> CreateGroup(int courseId)
    {
        Course? course = await courses.FindAsync(OrganizationId, courseId);
        if (course == null)
        {
            return NotFound();
        }

        // A sensible first suggestion: the next letter. The teacher starts as "Not decided yet": pre-selecting the
        // course's teacher assigned them to groups by accident when several groups were added in a row (QA, M3).
        var existing = (await courses.GetAsync(OrganizationId, courseId))!.Course.Groups.Select(g => g.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        string name = Enumerable.Range('A', 26).Select(c => ((char)c).ToString()).First(n => !existing.Contains(n));
        return View("EditGroup", await PrepareGroupAsync(new GroupViewModel
        {
            Name = name,
        }, course));
    }

    [HttpPost("{courseId:int}/groups/new")]
    public async Task<IActionResult> CreateGroup(int courseId, GroupViewModel model)
    {
        Course? course = await courses.FindAsync(OrganizationId, courseId);
        if (course == null)
        {
            return NotFound();
        }
        return await SaveGroupAsync(model, course, () => courses.CreateGroupAsync(OrganizationId, courseId, model.ToInput()));
    }

    [HttpGet("groups/{groupId:int}/edit")]
    public async Task<IActionResult> EditGroup(int groupId)
    {
        CourseGroup? group = await courses.FindGroupAsync(OrganizationId, groupId);
        if (group == null)
        {
            return NotFound();
        }

        var model = new GroupViewModel { Id = group.Id, Name = group.Name, Capacity = group.Capacity, TeacherId = group.TeacherId };
        return View(await PrepareGroupAsync(model, group.Course!, group.Enrolments.Count));
    }

    [HttpPost("groups/{groupId:int}/edit")]
    public async Task<IActionResult> EditGroup(int groupId, GroupViewModel model)
    {
        CourseGroup? group = await courses.FindGroupAsync(OrganizationId, groupId);
        if (group == null)
        {
            return NotFound();
        }

        model.Id = groupId;
        model.Enrolled = group.Enrolments.Count;
        return await SaveGroupAsync(model, group.Course!, () => courses.UpdateGroupAsync(OrganizationId, groupId, model.ToInput()));
    }

    [HttpPost("groups/{groupId:int}/delete")]
    public async Task<IActionResult> DeleteGroup(int groupId)
    {
        CourseGroup? group = await courses.FindGroupAsync(OrganizationId, groupId);
        ServiceResult result = await courses.DeleteGroupAsync(OrganizationId, groupId);
        return group == null
            ? ReportAndRedirect(result, nameof(Index))
            : ReportAndRedirect(result, nameof(Details), new { courseId = group.CourseId });
    }

    [HttpPost("groups/{groupId:int}/students")]
    public async Task<IActionResult> AddStudents(int groupId, AddStudentsViewModel model)
    {
        CourseGroup? group = await courses.FindGroupAsync(OrganizationId, groupId);
        if (group == null)
        {
            return ReportAndRedirect(ServiceResult.Fail("We couldn't find that group."), nameof(Index));
        }

        ServiceResult result = ModelState.IsValid
            ? await enrolments.EnrolManyAsync(OrganizationId, groupId, model.StudentIds)
            : ServiceResult.Fail("Tick the students to add.");
        return ReportAndRedirect(result, nameof(Details), new { courseId = group.CourseId });
    }

    private async Task<IActionResult> SaveGroupAsync(GroupViewModel model, Course course, Func<Task<ServiceResult>> save)
    {
        if (!ModelState.IsValid)
        {
            return View("EditGroup", await PrepareGroupAsync(model, course, model.Enrolled));
        }

        ServiceResult result = await save();
        if (!result.Succeeded)
        {
            ModelState.AddModelError("", result.Error!);
            return View("EditGroup", await PrepareGroupAsync(model, course, model.Enrolled));
        }
        return ReportAndRedirect(result, nameof(Details), new { courseId = course.Id });
    }

    private async Task<CourseViewModel> PrepareAsync(CourseViewModel model)
    {
        model.AvailableTeachers = await memberships.GetActivePeopleAsync(OrganizationId, MembershipRole.Teacher);
        model.Rules = await rules.GetAsync(OrganizationId);
        return model;
    }

    private async Task<GroupViewModel> PrepareGroupAsync(GroupViewModel model, Course course, int enrolled = 0)
    {
        var teacherIds = course.Teachers.Select(t => t.TeacherId).ToHashSet();
        model.Course = course;
        model.Enrolled = enrolled;
        model.CourseTeachers = (await memberships.GetActivePeopleAsync(OrganizationId, MembershipRole.Teacher))
            .Where(t => teacherIds.Contains(t.Id)).ToList();
        return model;
    }
}
