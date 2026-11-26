using Microsoft.EntityFrameworkCore;
using SecretariArt.Data;
using SecretariArt.Models;

namespace SecretariArt.Services;

public record SessionInput(int GroupId, int ClassroomId, DayOfWeek Day, TimeOnly Starts);

// A session as the editor shows it: with what's wrong with it now (rules, enrolments or rooms may have changed since).
public record SessionView(ClassSession Session, CourseGroup Group, Classroom Room, IReadOnlyList<string> Problems, IReadOnlyList<string> Warnings);

public record GroupProgress(CourseGroup Group, int Placed)
{
    public int Needed => Group.Course!.SessionsPerWeek;
    public bool Done => Placed >= Needed;
}

public record Suggestion(DayOfWeek Day, TimeOnly Starts, TimeOnly Ends, Classroom Room, IReadOnlyList<string> Warnings);

public record OrganizationSchedule(
    OrganizationRules Rules, IReadOnlyList<SessionView> Sessions, IReadOnlyList<GroupProgress> Groups, IReadOnlyList<Classroom> Rooms);

// One class in someone's own calendar, across all their organisations.
public record PersonalSession(ClassSession Session, Organization Organization, bool Teaching, string? Clash);

public interface IScheduleService
{
    Task<OrganizationSchedule> GetAsync(int organizationId);
    Task<SessionView?> FindAsync(int organizationId, int sessionId);
    Task<ScheduleCheck?> CheckAsync(int organizationId, SessionInput input, int? sessionId);
    Task<ServiceResult> CreateAsync(int organizationId, SessionInput input);
    Task<ServiceResult> UpdateAsync(int organizationId, int sessionId, SessionInput input);
    Task<ServiceResult> DeleteAsync(int organizationId, int sessionId);
    Task<List<Suggestion>> SuggestAsync(int organizationId, int groupId, int perDay = 3);
    Task<List<PersonalSession>> GetPersonalAsync(string userId);

    // Each class's number of problems and notes: compared before and after a change made elsewhere (e.g. Rules),
    // to tell the admin how many classes it just flagged.
    Task<IReadOnlyDictionary<int, int>> FlagsAsync(int organizationId);
}

public class ScheduleService(
    SecretariArtDbContext db, IRulesService rulesService, INotificationService notifications, TimeProvider clock) : IScheduleService
{
    public async Task<OrganizationSchedule> GetAsync(int organizationId)
    {
        ScheduleContext context = await LoadAsync(organizationId);
        var sessions = context.Sessions
            .Select(s => View(context, s))
            .OrderBy(v => Array.IndexOf(DisplayNames.Week, v.Session.Day)).ThenBy(v => v.Session.Starts)
            .ToList();
        var groups = context.Groups
            .Select(g => new GroupProgress(g, context.Sessions.Count(s => s.GroupId == g.Id)))
            .OrderBy(p => p.Done).ThenBy(p => p.Group.Course!.Name).ThenBy(p => p.Group.Name)
            .ToList();
        return new OrganizationSchedule(context.Rules, sessions, groups, context.Rooms);
    }

    public async Task<IReadOnlyDictionary<int, int>> FlagsAsync(int organizationId)
    {
        ScheduleContext context = await LoadAsync(organizationId);
        return context.Sessions.Select(s => View(context, s))
            .ToDictionary(v => v.Session.Id, v => v.Problems.Count + v.Warnings.Count);
    }

    public async Task<SessionView?> FindAsync(int organizationId, int sessionId)
    {
        ScheduleContext context = await LoadAsync(organizationId);
        ClassSession? session = context.Sessions.FirstOrDefault(s => s.Id == sessionId);
        return session == null ? null : View(context, session);
    }

    public async Task<ScheduleCheck?> CheckAsync(int organizationId, SessionInput input, int? sessionId)
    {
        ScheduleContext context = await LoadAsync(organizationId);
        return Check(context, input, sessionId);
    }

    public async Task<ServiceResult> CreateAsync(int organizationId, SessionInput input)
    {
        ScheduleContext context = await LoadAsync(organizationId);
        if (Check(context, input, null) is not ScheduleCheck check)
        {
            return NotFound;
        }
        if (!check.Allowed)
        {
            return ServiceResult.Fail(string.Join(" ", check.Problems));
        }

        CourseGroup group = context.Group(input.GroupId)!;
        Classroom room = context.Room(input.ClassroomId)!;
        var session = new ClassSession
        {
            OrganizationId = organizationId, GroupId = group.Id, ClassroomId = room.Id,
            Day = input.Day, Starts = input.Starts, Ends = check.Ends, UpdatedAt = Now,
        };
        db.ClassSessions.Add(session);
        Notify(context, group, $"New class: {Title(group)} on {When(session)} in {room.Name}.");
        await db.SaveChangesAsync();
        return Saved($"{Title(group)} added on {When(session)} in {room.Name}.", check, session.Id);
    }

    // Moves a session (day, time, room). The group stays: a different group is a different session.
    public async Task<ServiceResult> UpdateAsync(int organizationId, int sessionId, SessionInput input)
    {
        ScheduleContext context = await LoadAsync(organizationId);
        ClassSession? session = await db.ClassSessions.FirstOrDefaultAsync(s => s.Id == sessionId && s.OrganizationId == organizationId);
        if (session == null)
        {
            return SessionNotFound;
        }

        input = input with { GroupId = session.GroupId };
        if (Check(context, input, sessionId) is not ScheduleCheck check)
        {
            return NotFound;
        }
        if (!check.Allowed)
        {
            return ServiceResult.Fail(string.Join(" ", check.Problems));
        }

        CourseGroup group = context.Group(session.GroupId)!;
        Classroom room = context.Room(input.ClassroomId)!;
        string before = $"{When(session)} in {context.Room(session.ClassroomId)?.Name}";
        session.Day = input.Day;
        session.Starts = input.Starts;
        session.Ends = check.Ends;
        session.ClassroomId = room.Id;
        session.UpdatedAt = Now;
        string after = $"{When(session)} in {room.Name}";
        if (before == after)
        {
            return ServiceResult.Ok("Nothing changed.");
        }

        Notify(context, group, $"{Title(group)} moved from {before} to {after}.");
        await db.SaveChangesAsync();
        return Saved($"{Title(group)} moved to {after}. Its teacher and students have been told.", check);
    }

    public async Task<ServiceResult> DeleteAsync(int organizationId, int sessionId)
    {
        ScheduleContext context = await LoadAsync(organizationId);
        ClassSession? session = await db.ClassSessions.FirstOrDefaultAsync(s => s.Id == sessionId && s.OrganizationId == organizationId);
        if (session == null)
        {
            return SessionNotFound;
        }

        CourseGroup group = context.Group(session.GroupId)!;
        db.ClassSessions.Remove(session);
        Notify(context, group, $"{Title(group)} on {When(session)} has been taken off the timetable.");
        await db.SaveChangesAsync();
        return ServiceResult.Ok($"{Title(group)} on {When(session)} removed. Its teacher and students have been told.");
    }

    // Free slots for a group: every teaching day and start time on the grid, in the smallest suitable room that's
    // free. Slots without warnings come first (e.g. inside the teacher's availability).
    public async Task<List<Suggestion>> SuggestAsync(int organizationId, int groupId, int perDay = 3)
    {
        ScheduleContext context = await LoadAsync(organizationId);
        CourseGroup? group = context.Group(groupId);
        if (group == null)
        {
            return [];
        }

        OrganizationRules rules = context.Rules;
        var rooms = context.Rooms.OrderBy(r => r.Capacity).ThenBy(r => r.Name).ToList();
        var found = new List<Suggestion>();
        foreach (DayOfWeek day in rules.TeachingDays.Days())
        {
            for (TimeOnly starts = rules.DayStarts; starts.AddMinutes(group.Course!.SessionMinutes) <= rules.DayEnds && starts >= rules.DayStarts;
                 starts = starts.AddMinutes(rules.SlotMinutes))
            {
                TimeOnly ends = starts.AddMinutes(group.Course.SessionMinutes);
                if (ends <= starts)
                {
                    break; // past midnight
                }

                foreach (Classroom room in rooms)
                {
                    ScheduleCheck check = ScheduleChecker.Check(context, group, room, day, starts, ends, null);
                    if (check.Allowed)
                    {
                        found.Add(new Suggestion(day, starts, ends, room, check.Warnings));
                        break; // one room per slot is enough
                    }
                }
            }
        }

        // The best few on every teaching day (fewest notes, then on the hour, then earliest), so the whole week is
        // visible rather than the first morning minute by minute. Days when the group already meets come last: a
        // twice-a-week group is nudged onto a second day (QA, S9).
        var meets = context.Sessions.Where(s => s.GroupId == group.Id).Select(s => s.Day).ToHashSet();
        return found
            .GroupBy(s => s.Day)
            .OrderBy(d => meets.Contains(d.Key)).ThenBy(d => Array.IndexOf(DisplayNames.Week, d.Key))
            .SelectMany(d => d
                .OrderBy(s => s.Warnings.Count)
                .ThenBy(s => s.Starts.Minute == 0 ? 0 : 1)
                .ThenBy(s => s.Starts)
                .Take(perDay)
                .OrderBy(s => s.Starts))
            .ToList();
    }

    // Everything someone teaches or attends, in every organisation where they're active, with clashes marked.
    public async Task<List<PersonalSession>> GetPersonalAsync(string userId)
    {
        var memberships = await db.Memberships.AsNoTracking()
            .Where(m => m.UserId == userId && m.Status == MembershipStatus.Active && m.Organization!.Status == OrganizationStatus.Active)
            .Select(m => new { m.OrganizationId, m.Role }).ToListAsync();
        var teachingIn = memberships.Where(m => m.Role == MembershipRole.Teacher).Select(m => m.OrganizationId).ToHashSet();
        var studyingIn = memberships.Where(m => m.Role == MembershipRole.Student).Select(m => m.OrganizationId).ToHashSet();

        IQueryable<ClassSession> sessions = db.ClassSessions.AsNoTracking()
            .Include(s => s.Group).ThenInclude(g => g!.Course)
            .Include(s => s.Group).ThenInclude(g => g!.Teacher)
            .Include(s => s.Classroom);
        var taught = await sessions.Where(s => s.Group!.TeacherId == userId && teachingIn.Contains(s.OrganizationId)).ToListAsync();
        var attended = await sessions.Where(s => s.Group!.Enrolments.Any(e => e.StudentId == userId) && studyingIn.Contains(s.OrganizationId)).ToListAsync();

        var organizationIds = teachingIn.Union(studyingIn).ToList();
        var organizations = await db.Organizations.AsNoTracking().Where(o => organizationIds.Contains(o.Id)).ToDictionaryAsync(o => o.Id);
        var rules = await db.OrganizationRules.AsNoTracking().Where(r => organizationIds.Contains(r.OrganizationId)).ToDictionaryAsync(r => r.OrganizationId);

        var all = taught.Select(s => (Session: s, Teaching: true)).Concat(attended.Select(s => (Session: s, Teaching: false))).ToList();
        return all.Select(entry =>
            {
                var other = all.FirstOrDefault(o => !ReferenceEquals(o.Session, entry.Session) &&
                    o.Session.Overlaps(entry.Session.Day, entry.Session.Starts, entry.Session.Ends) &&
                    ScheduleChecker.TermsOverlap(rules.GetValueOrDefault(entry.Session.OrganizationId), rules.GetValueOrDefault(o.Session.OrganizationId)));
                string? clash = other.Session == null ? null
                    : $"Clashes with {other.Session.Group!.Course!.Name} at {organizations[other.Session.OrganizationId].Name} ({other.Session.Starts:HH:mm}–{other.Session.Ends:HH:mm}).";
                return new PersonalSession(entry.Session, organizations[entry.Session.OrganizationId], entry.Teaching, clash);
            })
            .OrderBy(p => Array.IndexOf(DisplayNames.Week, p.Session.Day)).ThenBy(p => p.Session.Starts)
            .ToList();
    }

    // ---- Helpers ----

    private static ScheduleCheck? Check(ScheduleContext context, SessionInput input, int? sessionId)
    {
        CourseGroup? group = context.Group(input.GroupId);
        Classroom? room = context.Room(input.ClassroomId);
        return group == null || room == null
            ? null
            : ScheduleChecker.Check(context, group, room, input.Day, input.Starts, input.Starts.AddMinutes(group.Course!.SessionMinutes), sessionId);
    }

    private static SessionView View(ScheduleContext context, ClassSession session)
    {
        CourseGroup group = context.Group(session.GroupId)!;
        Classroom room = context.Room(session.ClassroomId)!;
        ScheduleCheck check = ScheduleChecker.Check(context, group, room, session.Day, session.Starts, session.Ends, session.Id);
        // "Already has its sessions" only matters when adding; here the extra session itself is the problem.
        int placed = context.Sessions.Count(s => s.GroupId == group.Id);
        var warnings = check.Warnings.ToList();
        if (placed > group.Course!.SessionsPerWeek)
        {
            warnings.Add($"Group {group.Name} has {placed} sessions a week; {group.Course.Name} needs {group.Course.SessionsPerWeek}.");
        }
        return new SessionView(session, group, room, check.Problems, warnings);
    }

    private void Notify(ScheduleContext context, CourseGroup group, string text)
    {
        var people = group.Enrolments.Select(e => e.StudentId).ToList();
        if (group.TeacherId != null)
        {
            people.Add(group.TeacherId);
        }
        notifications.Add(people, context.Organization.Id, $"{context.Organization.Name}: {text}", "/schedule");
    }

    private static ServiceResult Saved(string message, ScheduleCheck check, int? createdId = null) =>
        ServiceResult.Ok(check.Warnings.Count == 0 ? message : $"{message} Note: {string.Join(" ", check.Warnings)}", createdId);

    private static string Title(CourseGroup group) => $"{group.Course!.Name} (group {group.Name})";

    private static string When(ClassSession session) => $"{session.Day} {session.Starts:HH:mm}–{session.Ends:HH:mm}";

    internal async Task<ScheduleContext> LoadAsync(int organizationId)
    {
        Organization organization = await db.Organizations.AsNoTracking().FirstAsync(o => o.Id == organizationId);
        OrganizationRules rules = await rulesService.GetAsync(organizationId);
        var rooms = await db.Classrooms.AsNoTracking().Where(r => r.OrganizationId == organizationId).ToListAsync();
        var groups = await db.CourseGroups.AsNoTracking().AsSplitQuery().Where(g => g.OrganizationId == organizationId)
            .Include(g => g.Course).Include(g => g.Teacher)
            .Include(g => g.Enrolments).ThenInclude(e => e.Student)
            .ToListAsync();
        var sessions = await db.ClassSessions.AsNoTracking().Where(s => s.OrganizationId == organizationId).ToListAsync();
        var availability = await db.TeacherAvailability.AsNoTracking().Where(a => a.OrganizationId == organizationId).ToListAsync();

        var teacherIds = groups.Where(g => g.TeacherId != null).Select(g => g.TeacherId!).Distinct().ToList();
        var people = groups.SelectMany(g => g.Enrolments.Select(e => e.StudentId)).Concat(teacherIds).Distinct().ToList();

        // Everyone who teaches or studies here: a student here may teach somewhere else (QA, S1).
        var taughtElsewhere = await db.ClassSessions.AsNoTracking().Include(s => s.Group)
            .Where(s => s.OrganizationId != organizationId && s.Group!.TeacherId != null && people.Contains(s.Group.TeacherId))
            .ToListAsync();
        // A join rather than SelectMany over the navigation: SQLite (the tests) can't translate that (SQL APPLY).
        var attendedElsewhere = (await (
                from e in db.Enrolments.AsNoTracking()
                where e.OrganizationId != organizationId && people.Contains(e.StudentId)
                join s in db.ClassSessions.AsNoTracking() on e.GroupId equals s.GroupId
                select new { e.StudentId, Session = s }).ToListAsync())
            .Select(a => (a.StudentId, a.Session)).ToList();

        var otherIds = taughtElsewhere.Select(s => s.OrganizationId).Concat(attendedElsewhere.Select(a => a.Session.OrganizationId)).Distinct().ToList();
        var otherRules = await db.OrganizationRules.AsNoTracking().Where(r => otherIds.Contains(r.OrganizationId))
            .ToDictionaryAsync(r => r.OrganizationId);

        return new ScheduleContext
        {
            Organization = organization, Rules = rules, Rooms = rooms, Groups = groups, Sessions = sessions,
            Availability = availability, TaughtElsewhere = taughtElsewhere, AttendedElsewhere = attendedElsewhere, OtherRules = otherRules,
        };
    }

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    private static readonly ServiceResult NotFound = ServiceResult.Fail("Choose a group and a classroom of this organisation.");
    private static readonly ServiceResult SessionNotFound = ServiceResult.Fail("We couldn't find that class in the timetable.");
}
