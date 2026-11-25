using Microsoft.AspNetCore.Mvc;
using SecretariArt.Extensions;
using SecretariArt.Services;
using SecretariArt.ViewModels;

namespace SecretariArt.Controllers;

// Enrolment from the student's side: one page per student with their courses, to add a course or a whole bundle.
// (Adding many students to one group at once is on the course page.)
[Route("admin/{organizationId:int}/enrolments")]
public class EnrolmentsController(
    IOrganizationService organizations,
    IEnrolmentService enrolments,
    ICourseService courses) : OrganizationAdminController(organizations)
{
    [HttpGet("")]
    public async Task<IActionResult> Index(string? search)
    {
        var students = await enrolments.GetStudentsAsync(OrganizationId);
        if (!string.IsNullOrWhiteSpace(search))
        {
            string term = search.Trim();
            students = students.Where(s =>
                TextMatch.Contains(s.Student.FullName, term) ||
                (s.Student.Email ?? "").Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();
        }

        return View(new EnrolmentsViewModel
        {
            Students = students,
            CourseCount = (await courses.GetAllAsync(OrganizationId)).Count,
            Search = search,
        });
    }

    [HttpGet("students/{studentId}")]
    public async Task<IActionResult> Student(string studentId)
    {
        StudentEnrolments? student = await enrolments.GetStudentAsync(OrganizationId, studentId);
        if (student == null)
        {
            return NotFound();
        }

        return View(new StudentEnrolmentsViewModel
        {
            Student = student,
            Courses = await courses.GetAllAsync(OrganizationId),
            Bundles = await courses.GetBundlesAsync(OrganizationId),
        });
    }

    [HttpPost("students/{studentId}/group")]
    public async Task<IActionResult> EnrolInGroup(string studentId, EnrolInGroupViewModel model) =>
        ReportAndRedirect(ModelState.IsValid
                ? await enrolments.EnrolAsync(OrganizationId, studentId, model.GroupId!.Value)
                : ServiceResult.Fail("Choose a course and group."),
            nameof(Student), new { studentId });

    [HttpPost("students/{studentId}/bundle")]
    public async Task<IActionResult> EnrolInBundle(string studentId, EnrolInBundleViewModel model) =>
        ReportAndRedirect(ModelState.IsValid
                ? await enrolments.EnrolInBundleAsync(OrganizationId, studentId, model.BundleId!.Value, model.PreferredGroup)
                : ServiceResult.Fail("Choose a bundle."),
            nameof(Student), new { studentId });

    // Used from the student page and from the course page; returns to whichever the admin came from.
    [HttpPost("{enrolmentId:int}/remove")]
    public async Task<IActionResult> Unenrol(int enrolmentId, string? returnUrl)
    {
        this.Report(await enrolments.UnenrolAsync(OrganizationId, enrolmentId));
        return Url.IsLocalUrl(returnUrl) ? LocalRedirect(returnUrl) : RedirectToAction(nameof(Index), OrganizationRoute());
    }
}
