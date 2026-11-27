using Microsoft.EntityFrameworkCore;
using SecretariArt.Data;
using SecretariArt.Models;

namespace SecretariArt.Services;

// A class in the draft, with what the checks say about it if the draft were in place.
public record DraftClass(ClassSession Session, CourseGroup Group, Classroom Room, bool IsNew, IReadOnlyList<string> Problems, IReadOnlyList<string> Warnings);

public record DraftGapView(CourseGroup Group, int Missing, string Reason);

// The draft as the admin sees it: every class of the week it would give (kept and new), what it couldn't place, and
// whether it still fits the timetable as it is now.
public record DraftView(ScheduleDraft Draft, ScheduleContext Context, IReadOnlyList<DraftClass> Classes, IReadOnlyList<DraftGapView> Gaps,
    IReadOnlyList<string> OutOfDate)
{
    // Start over only: check-ins to today's classes that using the draft would cancel, shown before choosing (QA, G2).
    public int CheckInsCancelled { get; init; }
    public int CurrentCount { get; init; }

    public int NewCount => Classes.Count(c => c.IsNew);
    public int Needed => Context.Groups.Sum(g => g.Course!.SessionsPerWeek);
    public bool CanUse => OutOfDate.Count == 0 && NewCount > 0;
}

public interface ITimetableDraftService
{
    Task<ServiceResult> GenerateAsync(int organizationId, GenerationMode mode, string userId, CancellationToken cancellationToken = default);
    Task<DraftView?> GetAsync(int organizationId);
    Task<bool> ExistsAsync(int organizationId);
    Task<ServiceResult> UseAsync(int organizationId);
    Task<ServiceResult> DiscardAsync(int organizationId);
}

public class TimetableDraftService(
    SecretariArtDbContext db, IScheduleService schedule, INotificationService notifications, TimeProvider clock, IConfiguration configuration,
    GenerationSlots slots)
    : ITimetableDraftService
{
    // Long enough for a school's week; the solver stops sooner when it has proved its answer is the best.
    private TimeSpan TimeLimit => TimeSpan.FromSeconds(configuration.GetValue("Generation:TimeLimitSeconds", 20.0));

    public async Task<ServiceResult> GenerateAsync(int organizationId, GenerationMode mode, string userId, CancellationToken cancellationToken = default)
    {
        ScheduleContext context = await schedule.LoadContextAsync(organizationId);
        if (context.Groups.Count == 0)
        {
            return ServiceResult.Fail("There are no groups to place yet: add courses with groups on the Courses tab first.");
        }
        if (context.Rooms.Count == 0)
        {
            return ServiceResult.Fail("There are no classrooms yet: add them on the Classrooms tab first.");
        }
        if (mode == GenerationMode.FillGaps && context.Groups.All(g => context.Sessions.Count(s => s.GroupId == g.Id) >= g.Course!.SessionsPerWeek))
        {
            return ServiceResult.Fail("Every group already has all its classes. To try a different week, choose “Start over”.");
        }

        // Only a few runs at a time across the whole server (GenerationSlots); the rest wait their turn briefly.
        if (!await slots.WaitAsync(cancellationToken))
        {
            return ServiceResult.Fail("Other timetables are being generated right now. Try again in a minute.");
        }
        TimetablePlan plan;
        try
        {
            // CPU work for a few seconds: off the request thread, so the server keeps answering others meanwhile.
            plan = await Task.Run(() => TimetableGenerator.Plan(context, mode, TimeLimit, cancellationToken), cancellationToken);
        }
        finally
        {
            slots.Release();
        }

        await db.ScheduleDrafts.Where(d => d.OrganizationId == organizationId).ExecuteDeleteAsync();
        var draft = new ScheduleDraft
        {
            OrganizationId = organizationId, Mode = mode, CreatedById = userId, CreatedAt = clock.GetUtcNow().UtcDateTime,
            Optimal = plan.Optimal, Milliseconds = plan.Milliseconds,
            Sessions = plan.Sessions.Select(p => new DraftSession
            {
                GroupId = p.GroupId, ClassroomId = p.ClassroomId, Day = p.Day, Starts = p.Starts, Ends = p.Ends,
                GroupLabel = context.Group(p.GroupId)?.Title() ?? "",
            }).ToList(),
            Gaps = plan.Gaps.Select(g => new DraftGap
            {
                GroupId = g.GroupId, Missing = g.Missing, Reason = DisplayNames.Truncate(g.Reason, TextLimits.Long),
            }).ToList(),
        };
        db.ScheduleDrafts.Add(draft);
        await db.SaveChangesAsync();

        int missing = plan.Gaps.Sum(g => g.Missing);
        return ServiceResult.Ok(
            $"Draft ready in {plan.Milliseconds / 1000.0:0.#} s: {DisplayNames.Plural(plan.Sessions.Count, "class", "classes")} placed" +
            (missing > 0 ? $", {missing} couldn't be (see why below)." : ".") +
            " Nothing has changed yet: look it over, then use it or discard it.");
    }

    public Task<bool> ExistsAsync(int organizationId) => db.ScheduleDrafts.AnyAsync(d => d.OrganizationId == organizationId);

    public async Task<DraftView?> GetAsync(int organizationId)
    {
        ScheduleDraft? draft = await db.ScheduleDrafts.AsNoTracking().Include(d => d.Sessions).Include(d => d.Gaps)
            .FirstOrDefaultAsync(d => d.OrganizationId == organizationId);
        if (draft == null)
        {
            return null;
        }
        ScheduleContext context = await schedule.LoadContextAsync(organizationId);
        return View(draft, context) with
        {
            CurrentCount = context.Sessions.Count,
            CheckInsCancelled = draft.Mode == GenerationMode.StartOver ? await UpcomingCheckInsAsync(context) : 0,
        };
    }

    // Puts the draft in the timetable, after checking it against the timetable as it is now: anything changed since
    // (a class added by hand, a room or group deleted, new enrolments) that makes it break a rule stops it.
    public async Task<ServiceResult> UseAsync(int organizationId)
    {
        ScheduleDraft? draft = await db.ScheduleDrafts.Include(d => d.Sessions)
            .FirstOrDefaultAsync(d => d.OrganizationId == organizationId);
        if (draft == null)
        {
            return ServiceResult.Fail("There's no draft to use: generate one first.");
        }
        ScheduleContext context = await schedule.LoadContextAsync(organizationId);
        DraftView view = View(draft, context);
        if (view.OutOfDate.Count > 0)
        {
            return ServiceResult.Fail("The timetable has changed since this draft was made, so it no longer fits: " +
                                      string.Join(" ", view.OutOfDate.Take(3)) + " Generate a new draft.");
        }
        if (view.NewCount == 0)
        {
            return ServiceResult.Fail("This draft has no classes to add.");
        }

        DateTime now = clock.GetUtcNow().UtcDateTime;
        var before = await db.ClassSessions.Where(s => s.OrganizationId == organizationId).ToListAsync();
        int cancelled = 0;
        if (draft.Mode == GenerationMode.StartOver)
        {
            // Upcoming check-ins were for classes that are going: cancelled, and the students told. Past ones stay.
            cancelled = await BookingService.CancelUpcomingAsync(db, notifications, clock, before.Select(s => s.Id).ToList(), "the timetable was rebuilt");
            db.ClassSessions.RemoveRange(before);
        }
        var added = draft.Sessions.Select(s => new ClassSession
        {
            OrganizationId = organizationId, GroupId = s.GroupId, ClassroomId = s.ClassroomId, Day = s.Day,
            Starts = s.Starts, Ends = s.Ends, UpdatedAt = now,
        }).ToList();
        db.ClassSessions.AddRange(added);
        db.ScheduleDrafts.Remove(draft);
        NotifyPeople(context, draft.Mode, before, added);
        await db.SaveChangesAsync();

        return ServiceResult.Ok(draft.Mode == GenerationMode.StartOver
            ? (before.Count == 0
                  ? $"The timetable is in place: {DisplayNames.Plural(added.Count, "weekly class", "weekly classes")}. Teachers and students have been told."
                  : $"The new timetable is in place: {DisplayNames.Plural(added.Count, "weekly class", "weekly classes")}, replacing {before.Count}. Teachers and students have been told.") +
              (cancelled > 0 ? $" {DisplayNames.Plural(cancelled, "upcoming check-in")} to the old classes {(cancelled == 1 ? "was" : "were")} cancelled." : "")
            : $"{DisplayNames.Plural(added.Count, "weekly class", "weekly classes")} added to the timetable. Their teachers and students have been told.");
    }

    public async Task<ServiceResult> DiscardAsync(int organizationId)
    {
        int deleted = await db.ScheduleDrafts.Where(d => d.OrganizationId == organizationId).ExecuteDeleteAsync();
        return deleted == 0 ? ServiceResult.Fail("There's no draft to discard.") : ServiceResult.Ok("Draft discarded. The timetable is as it was.");
    }

    // ---- Helpers ----

    // The week the draft would give, each class checked as if the draft were in place. Draft classes get negative ids
    // so the checks can tell them apart from the real ones.
    private static DraftView View(ScheduleDraft draft, ScheduleContext context)
    {
        var kept = draft.Mode == GenerationMode.FillGaps ? context.Sessions.ToList() : [];
        var outOfDate = new List<string>();
        var planned = new List<ClassSession>();
        foreach (var gone in draft.Sessions.Where(s => context.Group(s.GroupId) == null).GroupBy(s => s.GroupId))
        {
            int count = gone.Count();
            string label = gone.First().GroupLabel;
            outOfDate.Add(label.Length > 0
                ? $"{label} has been deleted since ({DisplayNames.Plural(count, "class", "classes")} in the draft)."
                // Drafts made before groups' names were stored.
                : $"{DisplayNames.Plural(count, "class", "classes")} in it {(count == 1 ? "belongs" : "belong")} to a group deleted since.");
        }
        foreach (var room in draft.Sessions.Where(s => context.Group(s.GroupId) != null && context.Room(s.ClassroomId) == null).GroupBy(s => s.ClassroomId))
        {
            var group = context.Group(room.First().GroupId)!;
            outOfDate.Add($"The classroom it gives {group.Title()} has been deleted since.");
        }
        foreach (DraftSession s in draft.Sessions)
        {
            if (context.Group(s.GroupId) == null || context.Room(s.ClassroomId) == null)
            {
                continue;
            }
            planned.Add(new ClassSession
            {
                Id = -s.Id, OrganizationId = draft.OrganizationId, GroupId = s.GroupId, ClassroomId = s.ClassroomId,
                Day = s.Day, Starts = s.Starts, Ends = s.Ends,
            });
        }

        ScheduleContext proposed = context.WithSessions(kept.Concat(planned).ToList());
        var classes = new List<DraftClass>();
        foreach (ClassSession session in kept.Concat(planned))
        {
            CourseGroup group = proposed.Group(session.GroupId)!;
            Classroom room = proposed.Room(session.ClassroomId)!;
            ScheduleCheck check = ScheduleChecker.Check(proposed, group, room, session.Day, session.Starts, session.Ends, session.Id);
            bool isNew = session.Id < 0;
            classes.Add(new DraftClass(session, group, room, isNew, check.Problems, check.Warnings));
            // Kept classes may already have problems of their own (they're flagged on the Schedule tab); only a new
            // class breaking a rule means the draft is out of date.
            if (isNew && check.Problems.Count > 0)
            {
                outOfDate.Add($"{group.Title()} on {session.Day} {session.Starts:HH:mm}: {check.Problems[0]}");
            }
        }

        var gaps = draft.Gaps.Select(g => (Gap: g, Group: context.Group(g.GroupId)))
            .Where(x => x.Group != null)
            .Select(x => new DraftGapView(x.Group!, x.Gap.Missing, x.Gap.Reason)).ToList();
        return new DraftView(draft, context,
            classes.OrderBy(c => c.Session.Day.WeekIndex()).ThenBy(c => c.Session.Starts).ToList(),
            gaps, outOfDate.Distinct().ToList());
    }

    // Check-ins to today's classes still to come: what using a Start over draft would cancel.
    private async Task<int> UpcomingCheckInsAsync(ScheduleContext context)
    {
        DateTime now = clock.SchoolNow();
        DateOnly today = DateOnly.FromDateTime(now);
        var ids = context.Sessions.Select(s => s.Id).ToList();
        var starts = context.Sessions.ToDictionary(s => s.Id, s => s.Starts);
        var upcoming = await db.Bookings.AsNoTracking()
            .Where(b => b.SessionId != null && ids.Contains(b.SessionId.Value) && b.CancelledAt == null && b.Date >= today)
            .Select(b => new { b.Date, SessionId = b.SessionId!.Value }).ToListAsync();
        return upcoming.Count(b => b.Date.ToDateTime(starts[b.SessionId]) > now);
    }

    // One notification per person, listing their classes in the new week (or the new ones, when filling gaps), rather
    // than one per class.
    private void NotifyPeople(ScheduleContext context, GenerationMode mode, IReadOnlyList<ClassSession> before, IReadOnlyList<ClassSession> added)
    {
        var touchedGroups = added.Select(s => s.GroupId)
            .Concat(mode == GenerationMode.StartOver ? before.Select(s => s.GroupId) : []).ToHashSet();
        var byPerson = new Dictionary<string, List<ClassSession>>();
        foreach (CourseGroup group in context.Groups.Where(g => touchedGroups.Contains(g.Id)))
        {
            var people = group.Enrolments.Select(e => e.StudentId).Append(group.TeacherId).OfType<string>();
            foreach (string person in people)
            {
                if (!byPerson.TryGetValue(person, out var list))
                {
                    byPerson[person] = list = [];
                }
                list.AddRange(added.Where(s => s.GroupId == group.Id));
            }
        }

        string organization = context.Organization.Name;
        foreach (var (person, classes) in byPerson)
        {
            // Like the single-class notices: course and group, day, time and room (QA, G3). The rest on their Schedule.
            var described = classes.OrderBy(s => s.Day.WeekIndex()).ThenBy(s => s.Starts)
                .Select(s =>
                {
                    CourseGroup group = context.Group(s.GroupId)!;
                    return $"{group.Title()} {s.When()} in {context.Room(s.ClassroomId)?.Name}";
                }).ToList();
            string list = described.Count == 0 ? "" :
                string.Join("; ", described.Take(3)) + (described.Count > 3 ? $"; and {described.Count - 3} more on your Schedule." : ".");
            string text = mode == GenerationMode.StartOver
                ? described.Count == 0
                    ? $"{organization}: The timetable has been rebuilt, and your classes there are off it for now."
                    : before.Count == 0
                        ? $"{organization}: The timetable is ready. Your classes: {list}"
                        : $"{organization}: The timetable has been rebuilt. Your classes: {list}"
                : described.Count == 1
                    ? $"{organization}: New class: {list}"
                    : $"{organization}: New classes: {list}";
            notifications.Add([person], context.Organization.Id, DisplayNames.Truncate(text, TextLimits.Long), "/schedule");
        }
    }
}
