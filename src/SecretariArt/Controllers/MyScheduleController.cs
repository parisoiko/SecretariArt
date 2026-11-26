using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using SecretariArt.Extensions;
using SecretariArt.Services;
using SecretariArt.ViewModels;

namespace SecretariArt.Controllers;

// Someone's own timetable: every class they teach or attend, in every organisation, in one week, with clashes in
// red; for teachers, the weekly availability they post for each organisation; and for per-class courses, checking in
// to classes (students) and who checked in (teachers).
[Route("schedule")]
public class MyScheduleController(
    ICurrentUserMemberships currentMemberships, IScheduleService schedule, IAvailabilityService availability, IBookingService bookings)
    : Controller
{
    private string UserId => User.FindFirstValue(ClaimTypes.NameIdentifier)!;

    [HttpGet("")]
    public async Task<IActionResult> Index(string? tab)
    {
        UserRoles roles = await currentMemberships.GetRolesAsync();
        if (!roles.IsTeacher && !roles.IsStudent)
        {
            return RedirectToAction("Index", "Home");
        }

        // Colours by organisation, in name order, the same on every tab.
        var organizations = (await currentMemberships.GetAsync())
            .Where(m => m.Status == Models.MembershipStatus.Active && m.Organization!.Status == Models.OrganizationStatus.Active &&
                        m.Role != Models.MembershipRole.Admin)
            .Select(m => m.Organization!).DistinctBy(o => o.Id).OrderBy(o => o.Name).ToList();
        var tones = organizations.Select((o, i) => (o.Id, i)).ToDictionary(x => x.Id, x => x.i);

        var toBook = roles.IsStudent ? await bookings.GetUpcomingForStudentAsync(UserId) : [];
        var teaching = roles.IsTeacher ? await bookings.GetForTeacherAsync(UserId) : [];
        bool canBook = toBook.Count > 0;
        bool hasCheckIns = teaching.Count > 0;

        if (tab == "availability" && roles.IsTeacher)
        {
            return View(new PersonalScheduleViewModel
            {
                Tab = "availability", Roles = roles, Tones = tones, Organizations = organizations, CanBook = canBook, HasCheckIns = hasCheckIns,
                Places = await availability.GetForTeacherAsync(UserId),
            });
        }
        if (tab == "book" && canBook)
        {
            return View(new PersonalScheduleViewModel
            {
                Tab = "book", Roles = roles, Tones = tones, Organizations = organizations, CanBook = true, HasCheckIns = hasCheckIns,
                Classes = toBook, CancelRules = await bookings.GetSettingsAsync(toBook.Select(c => c.Organization.Id)),
            });
        }
        if (tab == "check-ins" && hasCheckIns)
        {
            return View(new PersonalScheduleViewModel
            {
                Tab = "check-ins", Roles = roles, Tones = tones, Organizations = organizations, CanBook = canBook, HasCheckIns = true,
                Classes = teaching,
            });
        }

        var sessions = await schedule.GetPersonalAsync(UserId);
        var blocks = sessions.Select(p => new WeekBlock(
            p.Session.Day, p.Session.Starts, p.Session.Ends,
            p.Teaching ? $"{p.Session.Group!.Course!.Name} · teaching" : p.Session.Group!.Course!.Name,
            p.Organization.Name,
            p.Teaching
                ? $"Group {p.Session.Group.Name} · {p.Session.Classroom!.Name}"
                : $"{p.Session.Classroom!.Name}{(p.Session.Group.Teacher != null ? $" · {p.Session.Group.Teacher.FullName}" : "")}",
            null, tones.GetValueOrDefault(p.Organization.Id), p.Clash)).ToList();

        WeekGridModel grid = WeekGridModel.Fit(blocks, [], new TimeOnly(9, 0), new TimeOnly(18, 0), "My week");
        grid.EmptyText = "No classes yet. When your organisation puts your classes in its timetable, they appear here.";
        return View(new PersonalScheduleViewModel
        {
            Tab = "week", Roles = roles, Tones = tones, Organizations = organizations, Sessions = sessions, Grid = grid,
            CanBook = canBook, HasCheckIns = hasCheckIns,
        });
    }

    [HttpPost("availability")]
    public async Task<IActionResult> AddAvailability(AvailabilityFormViewModel model)
    {
        this.Report(ModelState.IsValid
            ? await availability.AddAsync(UserId, model.ToInput())
            : ServiceResult.Fail("Choose the organisation and the day, and enter both times."));
        return RedirectToAction(nameof(Index), new { tab = "availability" });
    }

    [HttpPost("availability/{availabilityId:int}/remove")]
    public async Task<IActionResult> RemoveAvailability(int availabilityId)
    {
        this.Report(await availability.RemoveAsync(UserId, availabilityId));
        return RedirectToAction(nameof(Index), new { tab = "availability" });
    }

    // ---- Checking in to per-class courses ----

    [HttpPost("book")]
    public async Task<IActionResult> Book(int sessionId, DateOnly date)
    {
        this.Report(await bookings.BookAsync(UserId, sessionId, date));
        return RedirectToAction(nameof(Index), new { tab = "book" });
    }

    [HttpPost("bookings/{bookingId:int}/cancel")]
    public async Task<IActionResult> CancelBooking(int bookingId)
    {
        this.Report(await bookings.CancelAsync(UserId, bookingId));
        return RedirectToAction(nameof(Index), new { tab = "book" });
    }

    // ---- A teacher's check-in lists ----

    [HttpGet("check-ins/{organizationId:int}/{sessionId:int}/{date}")]
    public async Task<IActionResult> Roster(int organizationId, int sessionId, DateOnly date)
    {
        ClassRoster? roster = await bookings.GetRosterAsync(organizationId, sessionId, date, UserId);
        return roster == null ? NotFound() : View("~/Views/Shared/Roster.cshtml", new RosterViewModel { Roster = roster, AsAdmin = false });
    }

    [HttpPost("check-ins/{organizationId:int}/bookings/{bookingId:int}")]
    public async Task<IActionResult> SetNoShow(int organizationId, int bookingId, bool noShow, int sessionId, DateOnly date)
    {
        this.Report(await bookings.SetNoShowAsync(organizationId, bookingId, noShow, UserId));
        return RedirectToAction(nameof(Roster), new { organizationId, sessionId, date = date.ToString("yyyy-MM-dd") });
    }
}
