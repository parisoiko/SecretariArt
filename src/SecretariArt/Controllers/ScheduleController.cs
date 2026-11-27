using Microsoft.AspNetCore.Mvc;
using SecretariArt.Models;
using SecretariArt.Services;
using SecretariArt.ViewModels;

namespace SecretariArt.Controllers;

// The admin's timetable editor: a week grid of every class, the groups still to place, and a form that checks for
// clashes before saving (ScheduleChecker). Free slots can be suggested for a group and used with one click.
[Route("admin/{organizationId:int}/schedule")]
public class ScheduleController(IOrganizationService organizations, IScheduleService schedule)
    : OrganizationAdminController(organizations)
{
    [HttpGet("")]
    public async Task<IActionResult> Index(int? roomId, string? teacherId, int? groupId, int? suggest)
    {
        OrganizationSchedule current = await schedule.GetAsync(OrganizationId);
        var form = new SessionFormViewModel { GroupId = suggest ?? groupId };
        return View(await BuildAsync(current, form, roomId, teacherId, groupId, suggest));
    }

    [HttpPost("")]
    public async Task<IActionResult> Create(SessionFormViewModel model)
    {
        if (ModelState.IsValid)
        {
            ServiceResult result = await schedule.CreateAsync(OrganizationId, model.ToInput());
            if (result.Succeeded)
            {
                return ReportAndRedirect(result, nameof(Index));
            }
            ModelState.AddModelError("", result.Error!);
        }

        // Refused: show the same page with the form as typed and the reasons above it.
        OrganizationSchedule current = await schedule.GetAsync(OrganizationId);
        return View(nameof(Index), await BuildAsync(current, model, null, null, null, null));
    }

    [HttpGet("{sessionId:int}/edit")]
    public async Task<IActionResult> Edit(int sessionId)
    {
        SessionView? view = await schedule.FindAsync(OrganizationId, sessionId);
        if (view == null)
        {
            return NotFound();
        }

        return View(await PrepareAsync(new SessionFormViewModel
        {
            SessionId = sessionId, GroupId = view.Group.Id, Day = view.Session.Day, Starts = view.Session.Starts,
            ClassroomId = view.Room.Id, Current = view,
        }));
    }

    [HttpPost("{sessionId:int}/edit")]
    public async Task<IActionResult> Edit(int sessionId, SessionFormViewModel model)
    {
        SessionView? view = await schedule.FindAsync(OrganizationId, sessionId);
        if (view == null)
        {
            return NotFound();
        }

        model.SessionId = sessionId;
        model.GroupId = view.Group.Id; // the group never changes; only when and where
        model.Current = view;
        ModelState.Remove(nameof(SessionFormViewModel.GroupId));
        if (ModelState.IsValid)
        {
            ServiceResult result = await schedule.UpdateAsync(OrganizationId, sessionId, model.ToInput());
            if (result.Succeeded)
            {
                return ReportAndRedirect(result, nameof(Index));
            }
            ModelState.AddModelError("", result.Error!);
        }
        return View(await PrepareAsync(model));
    }

    [HttpPost("{sessionId:int}/delete")]
    public async Task<IActionResult> Delete(int sessionId) =>
        ReportAndRedirect(await schedule.DeleteAsync(OrganizationId, sessionId), nameof(Index));

    private async Task<ScheduleViewModel> BuildAsync(
        OrganizationSchedule current, SessionFormViewModel form, int? roomId, string? teacherId, int? groupId, int? suggest)
    {
        // A stable colour per course, so the same course looks the same wherever it appears.
        var courseTone = current.Groups.Select(g => g.Group.CourseId).Distinct().Order()
            .Select((id, index) => (id, index)).ToDictionary(x => x.id, x => x.index);

        IEnumerable<SessionView> shown = current.Sessions;
        if (roomId != null) { shown = shown.Where(s => s.Room.Id == roomId); }
        if (teacherId != null) { shown = shown.Where(s => s.Group.TeacherId == teacherId); }
        if (groupId != null) { shown = shown.Where(s => s.Group.Id == groupId); }

        var blocks = shown.Select(s => new WeekBlock(
            s.Session.Day, s.Session.Starts, s.Session.Ends,
            $"{s.Group.Course!.Name} · {s.Group.Name}",
            s.Room.Name,
            s.Group.Teacher?.FullName ?? "No teacher yet",
            Url.Action(nameof(Edit), new { organizationId = OrganizationId, sessionId = s.Session.Id }),
            courseTone.GetValueOrDefault(s.Group.CourseId),
            // All of them, not just the first: the hover text and the phone list show every note (QA, S4).
            s.Problems.Count > 0 ? string.Join(" ", s.Problems) : null,
            s.Warnings.Count > 0 ? string.Join(" ", s.Warnings) : null,
            // Short blocks: who teaches it first, since that's what's looked for (QA, G6, G10).
            $"{s.Group.Teacher?.FirstName ?? "No teacher"} · {s.Room.Name}")).ToList();

        string label = roomId != null ? $"Timetable of {current.Rooms.FirstOrDefault(r => r.Id == roomId)?.Name}"
            : teacherId != null ? $"Timetable of {current.Groups.Select(g => g.Group.Teacher).FirstOrDefault(t => t?.Id == teacherId)?.FullName}"
            : groupId != null ? "Timetable of one group" : $"Timetable of {Organization.Name}";

        CourseGroup? suggestionsFor = suggest == null ? null : current.Groups.Select(g => g.Group).FirstOrDefault(g => g.Id == suggest);
        return new ScheduleViewModel
        {
            Schedule = current,
            Grid = WeekGridModel.Fit(blocks, current.Rules.TeachingDays.Days(), current.Rules.DayStarts, current.Rules.DayEnds, label),
            Form = await PrepareAsync(form, current),
            RoomId = roomId, TeacherId = teacherId, GroupId = groupId,
            SuggestionsFor = suggestionsFor,
            Suggestions = suggestionsFor == null ? null : await schedule.SuggestAsync(OrganizationId, suggestionsFor.Id),
        };
    }

    private async Task<SessionFormViewModel> PrepareAsync(SessionFormViewModel form, OrganizationSchedule? current = null)
    {
        current ??= await schedule.GetAsync(OrganizationId);
        form.Groups = current.Groups.Select(g => g.Group).OrderBy(g => g.Course!.Name).ThenBy(g => g.Name).ToList();
        form.Rooms = current.Rooms.OrderBy(r => r.Name).ToList();
        form.Rules = current.Rules;
        return form;
    }
}
