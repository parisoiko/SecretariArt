using SecretariArt.Models;

namespace SecretariArt.Services;

// Everything the conflict checks need about one organisation's timetable, loaded once (ScheduleService.LoadAsync)
// so a page can check every session, or try hundreds of slots for suggestions, without more queries.
public class ScheduleContext
{
    public required Organization Organization { get; init; }
    public required OrganizationRules Rules { get; init; }
    public required IReadOnlyList<Classroom> Rooms { get; init; }

    // With Course, Teacher and Enrolments (with Student).
    public required IReadOnlyList<CourseGroup> Groups { get; init; }
    public required IReadOnlyList<ClassSession> Sessions { get; init; }
    public required IReadOnlyList<TeacherAvailability> Availability { get; init; }

    // Other organisations' sessions of the same people: taught or attended by anyone who teaches or studies here.
    // Only counted when the terms overlap (OtherRules).
    public required IReadOnlyList<ClassSession> TaughtElsewhere { get; init; }
    public required IReadOnlyList<(string StudentId, ClassSession Session)> AttendedElsewhere { get; init; }
    public required IReadOnlyDictionary<int, OrganizationRules> OtherRules { get; init; }

    public CourseGroup? Group(int id) => Groups.FirstOrDefault(g => g.Id == id);
    public Classroom? Room(int id) => Rooms.FirstOrDefault(r => r.Id == id);

    // Teaching elsewhere only clashes if both organisations are teaching in the same weeks.
    public bool SameWeeks(int otherOrganizationId) =>
        ScheduleChecker.TermsOverlap(Rules, OtherRules.GetValueOrDefault(otherOrganizationId));

    // The same organisation with a different timetable: to check a draft as if it were in place.
    public ScheduleContext WithSessions(IReadOnlyList<ClassSession> sessions) => new()
    {
        Organization = Organization, Rules = Rules, Rooms = Rooms, Groups = Groups, Sessions = sessions, Availability = Availability,
        TaughtElsewhere = TaughtElsewhere, AttendedElsewhere = AttendedElsewhere, OtherRules = OtherRules,
    };
}

public record ScheduleCheck(TimeOnly Ends, IReadOnlyList<string> Problems, IReadOnlyList<string> Warnings)
{
    public bool Allowed => Problems.Count == 0;
}

// The rules of a timetable. Problems stop a session being saved; warnings are saved but shown, because the admin may
// know better (e.g. a teacher who hasn't posted availability yet but agreed by phone).
public static class ScheduleChecker
{
    public static ScheduleCheck Check(ScheduleContext context, CourseGroup group, Classroom room,
        DayOfWeek day, TimeOnly starts, TimeOnly ends, int? exceptSessionId)
    {
        var problems = new List<string>();
        var warnings = new List<string>();
        Course course = group.Course!;
        OrganizationRules rules = context.Rules;
        var others = context.Sessions.Where(s => s.Id != exceptSessionId).ToList();

        // ---- Rules ----
        if (!rules.TeachingDays.HasFlag(day.ToTeachingDay()))
        {
            problems.Add($"{context.Organization.Name} doesn't teach on {day}s (see Rules).");
        }
        if (ends <= starts || starts < rules.DayStarts || ends > rules.DayEnds)
        {
            problems.Add($"Classes must fit between {rules.DayStarts:HH:mm} and {rules.DayEnds:HH:mm} (see Rules); this one would end at {ends:HH:mm}.");
        }
        else if ((starts.ToTimeSpan() - rules.DayStarts.ToTimeSpan()).TotalMinutes % rules.SlotMinutes != 0)
        {
            problems.Add($"Classes start every {rules.SlotMinutes} minutes from {rules.DayStarts:HH:mm} (see Rules).");
        }

        // ---- Room ----
        RoomFeatures missing = course.RequiredFeatures & ~room.Features;
        if (missing != RoomFeatures.None)
        {
            problems.Add($"{room.Name} doesn't have {string.Join(", ", missing.Split().Select(f => f.Display().ToLowerInvariant()))}, which {course.Name} needs.");
        }
        // Per-class courses: check-ins never exceed the room (BookingService), so only the places matter.
        if (room.Capacity < group.Enrolments.Count && course.Pricing != CoursePricing.PerClass)
        {
            problems.Add($"{room.Name} holds {room.Capacity}, but group {group.Name} has {group.Enrolments.Count} students.");
        }
        else if (room.Capacity < group.Capacity)
        {
            warnings.Add($"{room.Name} holds {room.Capacity}, fewer than the group's {group.Capacity} places.");
        }

        int gap = rules.BreakBetweenSessionsMinutes;
        foreach (ClassSession other in others.Where(s => s.ClassroomId == room.Id && s.Overlaps(day, starts, ends, gap)))
        {
            bool onlyTheGap = !other.Overlaps(day, starts, ends);
            problems.Add($"{room.Name} is taken by {Describe(context, other)}" +
                         (onlyTheGap ? $", and the room needs {gap} minutes between classes (see Rules)." : "."));
        }

        // ---- The group itself ----
        if (others.Any(s => s.GroupId == group.Id && s.Overlaps(day, starts, ends)))
        {
            problems.Add($"Group {group.Name} of {course.Name} already meets then.");
        }

        // ---- Teacher ----
        ApplicationUser? teacher = group.Teacher;
        if (teacher == null)
        {
            warnings.Add($"Group {group.Name} has no teacher yet.");
        }
        else
        {
            foreach (ClassSession other in others.Where(s => s.GroupId != group.Id && context.Group(s.GroupId)?.TeacherId == teacher.Id &&
                                                             s.Overlaps(day, starts, ends)))
            {
                problems.Add($"{teacher.FullName} teaches {Describe(context, other)}.");
            }
            // The other class's own time: that's the one the admin can't see from here (QA, S8).
            if (context.TaughtElsewhere.FirstOrDefault(s => s.Group!.TeacherId == teacher.Id && s.Overlaps(day, starts, ends) &&
                                                            context.SameWeeks(s.OrganizationId)) is ClassSession elsewhere)
            {
                problems.Add($"{teacher.FullName} teaches at another organisation on {elsewhere.When()}.");
            }

            bool studiesThen =
                context.AttendedElsewhere.Any(a => a.StudentId == teacher.Id && a.Session.Overlaps(day, starts, ends) && context.SameWeeks(a.Session.OrganizationId)) ||
                others.Any(s => s.Overlaps(day, starts, ends) && context.Group(s.GroupId)!.Enrolments.Any(e => e.StudentId == teacher.Id));
            if (studiesThen)
            {
                warnings.Add($"{teacher.FullName} has a class of their own as a student then.");
            }

            var posted = context.Availability.Where(a => a.TeacherId == teacher.Id).ToList();
            if (posted.Count == 0)
            {
                warnings.Add($"{teacher.FullName} hasn't posted their availability yet.");
            }
            else if (!posted.Any(a => a.Covers(day, starts, ends)))
            {
                warnings.Add($"It's outside {teacher.FullName}'s availability.");
            }

            if (rules.MaxTeachingHoursPerDay is int maxHours)
            {
                double minutes = (ends - starts).TotalMinutes + others
                    .Where(s => s.Day == day && context.Group(s.GroupId)?.TeacherId == teacher.Id)
                    .Sum(s => (s.Ends - s.Starts).TotalMinutes);
                if (minutes > maxHours * 60)
                {
                    warnings.Add($"{teacher.FullName} would teach {DisplayNames.Minutes((int)minutes)} on {day}, more than the {maxHours} h limit (see Rules).");
                }
            }
        }

        // ---- Students ----
        var students = group.Enrolments.ToDictionary(e => e.StudentId, e => e.Student);
        var clashing = others
            .Where(s => s.GroupId != group.Id && s.Overlaps(day, starts, ends))
            .SelectMany(s => context.Group(s.GroupId)!.Enrolments.Where(e => students.ContainsKey(e.StudentId))
                .Select(e => (Student: students[e.StudentId], Session: s)))
            .ToList();
        if (clashing.Count > 0)
        {
            var names = clashing.Select(c => c.Student?.FullName ?? "a student").Distinct().ToList();
            problems.Add($"{Names(names)} also {(names.Count == 1 ? "takes" : "take")} {Describe(context, clashing[0].Session)}.");
        }

        // Busy elsewhere either way: a class they take there, or one they teach there.
        int busyElsewhere = context.AttendedElsewhere
            .Where(a => students.ContainsKey(a.StudentId) && a.Session.Overlaps(day, starts, ends) && context.SameWeeks(a.Session.OrganizationId))
            .Select(a => a.StudentId)
            .Concat(context.TaughtElsewhere
                .Where(s => students.ContainsKey(s.Group!.TeacherId!) && s.Overlaps(day, starts, ends) && context.SameWeeks(s.OrganizationId))
                .Select(s => s.Group!.TeacherId!))
            .Distinct().Count();
        if (busyElsewhere > 0)
        {
            warnings.Add($"{DisplayNames.Plural(busyElsewhere, "student")} {(busyElsewhere == 1 ? "has" : "have")} a class at another organisation then.");
        }

        // ---- Completeness ----
        int placed = others.Count(s => s.GroupId == group.Id);
        if (exceptSessionId == null && placed >= course.SessionsPerWeek)
        {
            warnings.Add($"Group {group.Name} already has its {DisplayNames.Plural(course.SessionsPerWeek, "session")} a week.");
        }
        if ((int)(ends - starts).TotalMinutes != course.SessionMinutes)
        {
            warnings.Add($"It lasts {DisplayNames.Minutes((int)(ends - starts).TotalMinutes)}, but {course.Name} sessions last {DisplayNames.Minutes(course.SessionMinutes)}.");
        }

        return new ScheduleCheck(ends, problems, warnings);
    }

    public static bool TermsOverlap(OrganizationRules? a, OrganizationRules? b) =>
        a?.TermStarts is not DateOnly aStarts || a.TermEnds is not DateOnly aEnds ||
        b?.TermStarts is not DateOnly bStarts || b.TermEnds is not DateOnly bEnds ||
        (aStarts <= bEnds && bStarts <= aEnds);

    public static string Describe(ScheduleContext context, ClassSession session)
    {
        CourseGroup? group = context.Group(session.GroupId);
        return $"{group?.Title()} on {session.When()}";
    }

    // "Anna", "Anna and Nikos", "Anna, Nikos and 3 others".
    private static string Names(List<string> names) => names.Count switch
    {
        1 => names[0],
        2 => $"{names[0]} and {names[1]}",
        3 => $"{names[0]}, {names[1]} and {names[2]}",
        _ => $"{names[0]}, {names[1]} and {names.Count - 2} others",
    };
}
