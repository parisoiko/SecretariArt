using Microsoft.EntityFrameworkCore;
using SecretariArt.Data;
using SecretariArt.Models;

namespace SecretariArt.Services;

public record CourseInput(string Name, string? Description, int SessionMinutes, int SessionsPerWeek,
    RoomFeatures RequiredFeatures, IReadOnlyCollection<string> TeacherIds);

public record GroupInput(string Name, int Capacity, string? TeacherId);

public record BundleInput(string Name, string? Description, IReadOnlyCollection<int> CourseIds);

// A course with what the admin should know before scheduling it: problems that won't stop saving, but would stop
// the schedule (no suitable room, no teacher, rules changed since).
public record CourseOverview(Course Course, IReadOnlyList<string> Warnings, IReadOnlyDictionary<int, string> GroupWarnings)
{
    public int Enrolled => Course.Groups.Sum(g => g.Enrolments.Count);
    public int Places => Course.Groups.Sum(g => g.Capacity);
}

public interface ICourseService
{
    Task<List<CourseOverview>> GetAllAsync(int organizationId);
    Task<CourseOverview?> GetAsync(int organizationId, int courseId);
    Task<Course?> FindAsync(int organizationId, int courseId);
    Task<ServiceResult> CreateAsync(int organizationId, CourseInput input);
    Task<ServiceResult> UpdateAsync(int organizationId, int courseId, CourseInput input);
    Task<ServiceResult> DeleteAsync(int organizationId, int courseId);

    Task<CourseGroup?> FindGroupAsync(int organizationId, int groupId);
    Task<ServiceResult> CreateGroupAsync(int organizationId, int courseId, GroupInput input);
    Task<ServiceResult> UpdateGroupAsync(int organizationId, int groupId, GroupInput input);
    Task<ServiceResult> DeleteGroupAsync(int organizationId, int groupId);

    Task<List<Bundle>> GetBundlesAsync(int organizationId);
    Task<Bundle?> FindBundleAsync(int organizationId, int bundleId);
    Task<ServiceResult> CreateBundleAsync(int organizationId, BundleInput input);
    Task<ServiceResult> UpdateBundleAsync(int organizationId, int bundleId, BundleInput input);
    Task<ServiceResult> DeleteBundleAsync(int organizationId, int bundleId);
}

public class CourseService(
    SecretariArtDbContext db, IRulesService rulesService, IMembershipService memberships, INotificationService notifications) : ICourseService
{
    // ---- Courses ----

    public async Task<List<CourseOverview>> GetAllAsync(int organizationId)
    {
        List<Course> courses = await CoursesWithDetails(organizationId).OrderBy(c => c.Name).ToListAsync();
        var context = await WarningContextAsync(organizationId);
        return courses.Select(c => Overview(c, context)).ToList();
    }

    public async Task<CourseOverview?> GetAsync(int organizationId, int courseId)
    {
        Course? course = await CoursesWithDetails(organizationId)
            .Include(c => c.Groups).ThenInclude(g => g.Enrolments).ThenInclude(e => e.Student)
            .Include(c => c.Groups).ThenInclude(g => g.Enrolments).ThenInclude(e => e.Bundle)
            // For the delete confirmations: how many weekly classes would come off the timetable (QA, S5).
            .Include(c => c.Groups).ThenInclude(g => g.Sessions)
            .FirstOrDefaultAsync(c => c.Id == courseId);
        return course == null ? null : Overview(course, await WarningContextAsync(organizationId));
    }

    public Task<Course?> FindAsync(int organizationId, int courseId) =>
        db.Courses.AsNoTracking().Include(c => c.Teachers)
            .FirstOrDefaultAsync(c => c.Id == courseId && c.OrganizationId == organizationId);

    public async Task<ServiceResult> CreateAsync(int organizationId, CourseInput input)
    {
        if (await CheckCourseAsync(organizationId, null, input) is ServiceResult problem)
        {
            return problem;
        }

        var course = new Course { OrganizationId = organizationId };
        Apply(course, input);
        db.Courses.Add(course);
        await db.SaveChangesAsync();
        return ServiceResult.Ok($"{course.Name} added. Add a group to it, so students can be enrolled.", course.Id);
    }

    public async Task<ServiceResult> UpdateAsync(int organizationId, int courseId, CourseInput input)
    {
        Course? course = await db.Courses.Include(c => c.Teachers).Include(c => c.Groups)
            .FirstOrDefaultAsync(c => c.Id == courseId && c.OrganizationId == organizationId);
        if (course == null)
        {
            return CourseNotFound;
        }
        if (await CheckCourseAsync(organizationId, courseId, input) is ServiceResult problem)
        {
            return problem;
        }

        // A teacher taken off the course stops teaching its groups too, rather than teaching a course they're not on.
        var removed = course.Teachers.Select(t => t.TeacherId).Except(input.TeacherIds).ToHashSet();
        int orphaned = 0;
        foreach (CourseGroup group in course.Groups.Where(g => g.TeacherId != null && removed.Contains(g.TeacherId)))
        {
            group.TeacherId = null;
            orphaned++;
        }

        // Sessions already in the timetable keep their times; a new length applies when each is moved.
        int oldLength = course.SessionMinutes;
        Apply(course, input);
        // Classes whose length now differs from the course's (QA, S3: putting the length back must count none).
        int stale = 0;
        if (oldLength != course.SessionMinutes)
        {
            var sessions = await db.ClassSessions.Where(s => s.Group!.CourseId == courseId).Select(s => new { s.Starts, s.Ends }).ToListAsync();
            stale = sessions.Count(s => (int)(s.Ends - s.Starts).TotalMinutes != course.SessionMinutes);
        }
        await db.SaveChangesAsync();
        return ServiceResult.Ok($"{course.Name} saved." +
            (orphaned > 0 ? $" {orphaned} group{(orphaned == 1 ? " has" : "s have")} no teacher now: choose one on the course page." : "") +
            (stale > 0
                ? stale == 1
                    ? $" 1 class in the timetable doesn't last {DisplayNames.Minutes(course.SessionMinutes)} yet: it's flagged on the Schedule tab until you move it."
                    : $" {stale} classes in the timetable don't last {DisplayNames.Minutes(course.SessionMinutes)} yet: they're flagged on the Schedule tab until you move them."
                : ""));
    }

    // Removes the course with its groups and its students' places in them; the confirmation says how many.
    public async Task<ServiceResult> DeleteAsync(int organizationId, int courseId)
    {
        Course? course = await db.Courses.Include(c => c.Groups)
            .FirstOrDefaultAsync(c => c.Id == courseId && c.OrganizationId == organizationId);
        if (course == null)
        {
            return CourseNotFound;
        }

        var enrolments = await db.Enrolments.Where(e => e.CourseId == courseId).ToListAsync();
        var sessions = await db.ClassSessions.Where(s => s.Group!.CourseId == courseId).ToListAsync();
        if (sessions.Count > 0)
        {
            var people = enrolments.Select(e => e.StudentId).Concat(course.Groups.Where(g => g.TeacherId != null).Select(g => g.TeacherId!));
            notifications.Add(people, organizationId, $"{await OrganizationNameAsync(organizationId)}: {course.Name} has been cancelled; its classes are off the timetable.", "/schedule");
        }
        db.ClassSessions.RemoveRange(sessions);
        db.Enrolments.RemoveRange(enrolments);
        db.CourseGroups.RemoveRange(course.Groups);
        db.Courses.Remove(course);
        await db.SaveChangesAsync();
        return ServiceResult.Ok($"{course.Name} deleted" +
            (enrolments.Count > 0 ? $", with its {DisplayNames.Plural(course.Groups.Count, "group")} and {DisplayNames.Plural(enrolments.Count, "enrolment")}" : "") +
            (sessions.Count > 0 ? $"; {DisplayNames.Plural(sessions.Count, "class", "classes")} came off the timetable and the people concerned have been told." : "."));
    }

    // ---- Groups ----

    public Task<CourseGroup?> FindGroupAsync(int organizationId, int groupId) =>
        db.CourseGroups.AsNoTracking().Include(g => g.Course).ThenInclude(c => c!.Teachers)
            .Include(g => g.Enrolments)
            .FirstOrDefaultAsync(g => g.Id == groupId && g.OrganizationId == organizationId);

    public async Task<ServiceResult> CreateGroupAsync(int organizationId, int courseId, GroupInput input)
    {
        Course? course = await db.Courses.Include(c => c.Teachers).Include(c => c.Groups)
            .FirstOrDefaultAsync(c => c.Id == courseId && c.OrganizationId == organizationId);
        if (course == null)
        {
            return CourseNotFound;
        }
        if (CheckGroup(course, null, input, enrolled: 0) is ServiceResult problem)
        {
            return problem;
        }

        var group = new CourseGroup { OrganizationId = organizationId, CourseId = courseId };
        Apply(group, input);
        db.CourseGroups.Add(group);
        await db.SaveChangesAsync();
        return ServiceResult.Ok($"Group {group.Name} added to {course.Name}.");
    }

    public async Task<ServiceResult> UpdateGroupAsync(int organizationId, int groupId, GroupInput input)
    {
        CourseGroup? group = await db.CourseGroups
            .Include(g => g.Course).ThenInclude(c => c!.Teachers)
            .Include(g => g.Course).ThenInclude(c => c!.Groups)
            .FirstOrDefaultAsync(g => g.Id == groupId && g.OrganizationId == organizationId);
        if (group == null)
        {
            return GroupNotFound;
        }

        int enrolled = await db.Enrolments.CountAsync(e => e.GroupId == groupId);
        if (CheckGroup(group.Course!, groupId, input, enrolled) is ServiceResult problem)
        {
            return problem;
        }
        // The other side of "a teacher can't be a student in their own group".
        if (input.TeacherId != null && await db.Enrolments.AnyAsync(e => e.GroupId == groupId && e.StudentId == input.TeacherId))
        {
            return ServiceResult.Fail("That teacher is enrolled as a student in this group. Remove them from it first, or choose another teacher.");
        }

        // Sessions follow the group's teacher, so a new teacher must be free at the group's class times.
        string? previousTeacher = group.TeacherId;
        string? newTeacher = string.IsNullOrEmpty(input.TeacherId) ? null : input.TeacherId;
        bool teacherChanges = newTeacher != previousTeacher;
        if (teacherChanges && newTeacher != null && await TimetableClashes.ForTeacherAsync(db, newTeacher, group) is string clash)
        {
            return ServiceResult.Fail($"That teacher already teaches {clash}, when this group meets. Choose another teacher, or move the classes on the Schedule tab.");
        }

        Apply(group, input);
        if (teacherChanges && await db.ClassSessions.AnyAsync(s => s.GroupId == groupId))
        {
            string organization = await OrganizationNameAsync(organizationId);
            string title = $"{group.Course!.Name} (group {group.Name})";
            if (newTeacher != null)
            {
                notifications.Add([newTeacher], organizationId, $"{organization}: you now teach {title}. It's in your Schedule.", "/schedule");
            }
            if (previousTeacher != null)
            {
                notifications.Add([previousTeacher], organizationId, $"{organization}: you no longer teach {title}.", "/schedule");
            }
        }
        await db.SaveChangesAsync();
        return ServiceResult.Ok($"Group {group.Name} saved.");
    }

    public async Task<ServiceResult> DeleteGroupAsync(int organizationId, int groupId)
    {
        CourseGroup? group = await db.CourseGroups.Include(g => g.Course).Include(g => g.Enrolments)
            .FirstOrDefaultAsync(g => g.Id == groupId && g.OrganizationId == organizationId);
        if (group == null)
        {
            return GroupNotFound;
        }

        int enrolled = group.Enrolments.Count;
        var sessions = await db.ClassSessions.Where(s => s.GroupId == groupId).ToListAsync();
        if (sessions.Count > 0)
        {
            var people = group.Enrolments.Select(e => e.StudentId).Concat(group.TeacherId == null ? [] : [group.TeacherId]);
            notifications.Add(people, organizationId,
                $"{await OrganizationNameAsync(organizationId)}: group {group.Name} of {group.Course!.Name} has been closed; its classes are off the timetable.", "/schedule");
        }
        db.ClassSessions.RemoveRange(sessions);
        db.Enrolments.RemoveRange(group.Enrolments);
        db.CourseGroups.Remove(group);
        await db.SaveChangesAsync();
        return ServiceResult.Ok($"Group {group.Name} of {group.Course!.Name} deleted" +
            (enrolled > 0 ? $"; {(enrolled == 1 ? "its 1 student is" : $"its {enrolled} students are")} no longer enrolled in the course" : "") +
            (sessions.Count > 0 ? $"; {DisplayNames.Plural(sessions.Count, "class", "classes")} came off the timetable and the people concerned have been told." : "."));
    }

    // ---- Bundles ----

    public Task<List<Bundle>> GetBundlesAsync(int organizationId) =>
        db.Bundles.AsNoTracking().Where(b => b.OrganizationId == organizationId)
            .Include(b => b.Courses).ThenInclude(l => l.Course).ThenInclude(c => c!.Groups)
            .OrderBy(b => b.Name).ToListAsync();

    public Task<Bundle?> FindBundleAsync(int organizationId, int bundleId) =>
        db.Bundles.AsNoTracking()
            .Include(b => b.Courses).ThenInclude(l => l.Course).ThenInclude(c => c!.Groups)
            .FirstOrDefaultAsync(b => b.Id == bundleId && b.OrganizationId == organizationId);

    public async Task<ServiceResult> CreateBundleAsync(int organizationId, BundleInput input)
    {
        if (await CheckBundleAsync(organizationId, null, input) is ServiceResult problem)
        {
            return problem;
        }

        var bundle = new Bundle { OrganizationId = organizationId };
        Apply(bundle, input);
        db.Bundles.Add(bundle);
        await db.SaveChangesAsync();
        return ServiceResult.Ok($"{bundle.Name} added, with {DisplayNames.Plural(input.CourseIds.Count, "course")}.");
    }

    public async Task<ServiceResult> UpdateBundleAsync(int organizationId, int bundleId, BundleInput input)
    {
        Bundle? bundle = await db.Bundles.Include(b => b.Courses)
            .FirstOrDefaultAsync(b => b.Id == bundleId && b.OrganizationId == organizationId);
        if (bundle == null)
        {
            return BundleNotFound;
        }
        if (await CheckBundleAsync(organizationId, bundleId, input) is ServiceResult problem)
        {
            return problem;
        }

        Apply(bundle, input);
        await db.SaveChangesAsync();
        return ServiceResult.Ok($"{bundle.Name} saved. Students already enrolled through it keep their places.");
    }

    public async Task<ServiceResult> DeleteBundleAsync(int organizationId, int bundleId)
    {
        Bundle? bundle = await db.Bundles.FirstOrDefaultAsync(b => b.Id == bundleId && b.OrganizationId == organizationId);
        if (bundle == null)
        {
            return BundleNotFound;
        }

        // Places taken through the bundle stay (the database sets their BundleId to null); load them so EF does the
        // same for tracked rows on providers that don't.
        await db.Enrolments.Where(e => e.BundleId == bundleId).LoadAsync();
        db.Bundles.Remove(bundle);
        await db.SaveChangesAsync();
        return ServiceResult.Ok($"{bundle.Name} deleted. Students enrolled through it keep their courses.");
    }

    // ---- Checks ----

    private async Task<ServiceResult?> CheckCourseAsync(int organizationId, int? courseId, CourseInput input)
    {
        // Same name ignoring case and accents (TextMatch): "ΘΕΑΤΡΟ" is "Θέατρο".
        var others = await db.Courses.Where(c => c.OrganizationId == organizationId && c.Id != courseId).Select(c => c.Name).ToListAsync();
        if (others.Any(n => TextMatch.SameName(n, input.Name)))
        {
            return ServiceResult.Fail($"There's already a course called {input.Name.Trim()}.");
        }

        OrganizationRules rules = await rulesService.GetAsync(organizationId);
        if (CourseRules.Problems(input.SessionMinutes, input.SessionsPerWeek, rules).FirstOrDefault() is string problem)
        {
            return ServiceResult.Fail(problem);
        }

        // Only this organisation's approved teachers: a posted id from elsewhere is refused, not silently dropped.
        var teachers = (await memberships.GetActivePeopleAsync(organizationId, MembershipRole.Teacher)).Select(t => t.Id).ToHashSet();
        if (input.TeacherIds.Any(id => !teachers.Contains(id)))
        {
            return ServiceResult.Fail("Choose teachers from this organisation's approved teachers.");
        }
        return null;
    }

    private static ServiceResult? CheckGroup(Course course, int? groupId, GroupInput input, int enrolled)
    {
        string name = input.Name.Trim();
        if (course.Groups.Any(g => g.Id != groupId && TextMatch.SameName(g.Name, name)))
        {
            return ServiceResult.Fail($"{course.Name} already has a group called {name}.");
        }
        if (input.Capacity < enrolled && course.Pricing != CoursePricing.PerClass)
        {
            return ServiceResult.Fail($"Group {name} already has {DisplayNames.Plural(enrolled, "student")}; its places can't be fewer.");
        }
        if (input.TeacherId != null && course.Teachers.All(t => t.TeacherId != input.TeacherId))
        {
            return ServiceResult.Fail($"Choose one of {course.Name}'s teachers, or add the teacher to the course first.");
        }
        return null;
    }

    private async Task<ServiceResult?> CheckBundleAsync(int organizationId, int? bundleId, BundleInput input)
    {
        var others = await db.Bundles.Where(b => b.OrganizationId == organizationId && b.Id != bundleId).Select(b => b.Name).ToListAsync();
        if (others.Any(n => TextMatch.SameName(n, input.Name)))
        {
            return ServiceResult.Fail($"There's already a bundle called {input.Name.Trim()}.");
        }
        if (input.CourseIds.Count == 0)
        {
            return ServiceResult.Fail("Choose at least one course for the bundle.");
        }

        int found = await db.Courses.CountAsync(c => c.OrganizationId == organizationId && input.CourseIds.Contains(c.Id));
        return found == input.CourseIds.Distinct().Count() ? null : ServiceResult.Fail("Choose courses from this organisation.");
    }

    // ---- Helpers ----

    private IQueryable<Course> CoursesWithDetails(int organizationId) =>
        db.Courses.AsNoTracking().AsSplitQuery()
            .Where(c => c.OrganizationId == organizationId)
            .Include(c => c.Teachers).ThenInclude(t => t.Teacher)
            .Include(c => c.Groups).ThenInclude(g => g.Teacher)
            .Include(c => c.Groups).ThenInclude(g => g.Enrolments)
            .Include(c => c.Bundles).ThenInclude(l => l.Bundle);

    private record WarningContext(OrganizationRules Rules, List<Classroom> Classrooms);

    private async Task<WarningContext> WarningContextAsync(int organizationId) =>
        new(await rulesService.GetAsync(organizationId),
            await db.Classrooms.AsNoTracking().Where(c => c.OrganizationId == organizationId).ToListAsync());

    private static CourseOverview Overview(Course course, WarningContext context)
    {
        course.Groups = course.Groups.OrderBy(g => g.Name).ToList();
        var warnings = CourseRules.Problems(course.SessionMinutes, course.SessionsPerWeek, context.Rules).ToList();

        var suitable = context.Classrooms.Where(r => r.Features.HasFlag(course.RequiredFeatures)).ToList();
        if (suitable.Count == 0)
        {
            warnings.Add(context.Classrooms.Count == 0
                ? "There are no classrooms yet."
                : $"No classroom has everything it needs ({string.Join(", ", course.RequiredFeatures.Split().Select(f => f.Display()))}).");
        }
        if (course.Teachers.Count == 0)
        {
            warnings.Add("No teacher is assigned to it yet.");
        }
        if (course.Groups.Count == 0)
        {
            warnings.Add("It has no groups yet, so no one can be enrolled.");
        }

        int largestRoom = suitable.Count == 0 ? 0 : suitable.Max(r => r.Capacity);
        var groupWarnings = new Dictionary<int, string>();
        foreach (CourseGroup group in course.Groups)
        {
            if (suitable.Count > 0 && group.Capacity > largestRoom)
            {
                groupWarnings[group.Id] = $"{group.Capacity} places, but the largest suitable room holds {largestRoom}.";
            }
            // When the course has no teachers at all, the course-level warning already says so.
            else if (group.TeacherId == null && course.Teachers.Count > 0)
            {
                groupWarnings[group.Id] = "No teacher yet.";
            }
        }

        return new CourseOverview(course, warnings, groupWarnings);
    }

    private static void Apply(Course course, CourseInput input)
    {
        course.Name = input.Name.Trim();
        course.Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();
        course.SessionMinutes = input.SessionMinutes;
        course.SessionsPerWeek = input.SessionsPerWeek;
        course.RequiredFeatures = input.RequiredFeatures;

        course.Teachers.RemoveAll(t => !input.TeacherIds.Contains(t.TeacherId));
        foreach (string teacherId in input.TeacherIds.Distinct().Where(id => course.Teachers.All(t => t.TeacherId != id)))
        {
            course.Teachers.Add(new CourseTeacher { TeacherId = teacherId });
        }
    }

    private static void Apply(CourseGroup group, GroupInput input)
    {
        group.Name = input.Name.Trim();
        group.Capacity = input.Capacity;
        group.TeacherId = string.IsNullOrEmpty(input.TeacherId) ? null : input.TeacherId;
    }

    private static void Apply(Bundle bundle, BundleInput input)
    {
        bundle.Name = input.Name.Trim();
        bundle.Description = string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim();
        bundle.Courses.RemoveAll(l => !input.CourseIds.Contains(l.CourseId));
        foreach (int courseId in input.CourseIds.Distinct().Where(id => bundle.Courses.All(l => l.CourseId != id)))
        {
            bundle.Courses.Add(new BundleCourse { CourseId = courseId });
        }
    }


    private async Task<string> OrganizationNameAsync(int organizationId) =>
        (await db.Organizations.AsNoTracking().FirstAsync(o => o.Id == organizationId)).Name;

    private static readonly ServiceResult CourseNotFound = ServiceResult.Fail("We couldn't find that course.");
    private static readonly ServiceResult GroupNotFound = ServiceResult.Fail("We couldn't find that group.");
    private static readonly ServiceResult BundleNotFound = ServiceResult.Fail("We couldn't find that bundle.");
}
