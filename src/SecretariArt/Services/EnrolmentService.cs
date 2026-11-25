using Microsoft.EntityFrameworkCore;
using SecretariArt.Data;
using SecretariArt.Models;

namespace SecretariArt.Services;

// A student of the organisation with their places, for the Enrolments tab.
public record StudentEnrolments(ApplicationUser Student, IReadOnlyList<Enrolment> Enrolments);

public interface IEnrolmentService
{
    Task<List<StudentEnrolments>> GetStudentsAsync(int organizationId);
    Task<StudentEnrolments?> GetStudentAsync(int organizationId, string studentId);
    Task<ServiceResult> EnrolAsync(int organizationId, string studentId, int groupId);
    Task<ServiceResult> EnrolManyAsync(int organizationId, int groupId, IReadOnlyCollection<string> studentIds);
    Task<ServiceResult> EnrolInBundleAsync(int organizationId, string studentId, int bundleId, string? preferredGroup);
    Task<ServiceResult> UnenrolAsync(int organizationId, int enrolmentId);
}

public class EnrolmentService(SecretariArtDbContext db, IMembershipService memberships, TimeProvider clock) : IEnrolmentService
{
    public async Task<List<StudentEnrolments>> GetStudentsAsync(int organizationId)
    {
        List<ApplicationUser> students = await memberships.GetActivePeopleAsync(organizationId, MembershipRole.Student);
        var byStudent = (await EnrolmentsOf(organizationId).ToListAsync()).ToLookup(e => e.StudentId);
        return students.Select(s => new StudentEnrolments(s, Sorted(byStudent[s.Id]))).ToList();
    }

    public async Task<StudentEnrolments?> GetStudentAsync(int organizationId, string studentId)
    {
        ApplicationUser? student = (await memberships.GetActivePeopleAsync(organizationId, MembershipRole.Student))
            .FirstOrDefault(s => s.Id == studentId);
        if (student == null)
        {
            return null;
        }

        var enrolments = await EnrolmentsOf(organizationId).Where(e => e.StudentId == studentId).ToListAsync();
        return new StudentEnrolments(student, Sorted(enrolments));
    }

    // Puts the student in the group. If they already take the course in another group, they move (keeping how they
    // got there, e.g. through a bundle).
    public async Task<ServiceResult> EnrolAsync(int organizationId, string studentId, int groupId)
    {
        CourseGroup? group = await GroupWithPlacesAsync(organizationId, groupId);
        if (group == null)
        {
            return GroupNotFound;
        }
        if (await StudentNameAsync(organizationId, studentId) is not string name)
        {
            return NotAStudent;
        }

        Enrolment? current = await db.Enrolments.Include(e => e.Group)
            .FirstOrDefaultAsync(e => e.StudentId == studentId && e.CourseId == group.CourseId);
        if (current?.GroupId == groupId)
        {
            return ServiceResult.Fail($"{name} is already in group {group.Name} of {group.Course!.Name}.");
        }
        if (group.TeacherId == studentId)
        {
            return TeachesGroup(name, group);
        }
        if (group.Enrolments.Count >= group.Capacity)
        {
            return Full(group);
        }

        if (current != null)
        {
            string from = current.Group!.Name;
            current.GroupId = groupId;
            await db.SaveChangesAsync();
            return ServiceResult.Ok($"{name} moved from group {from} to group {group.Name} of {group.Course!.Name}.");
        }

        db.Enrolments.Add(NewEnrolment(organizationId, studentId, group, bundleId: null));
        await db.SaveChangesAsync();
        return ServiceResult.Ok($"{name} enrolled in {group.Course!.Name}, group {group.Name}.");
    }

    // From a group's page: add several students at once. Those already taking the course (in any group) are
    // skipped rather than moved, since moving people is a decision to make one by one.
    public async Task<ServiceResult> EnrolManyAsync(int organizationId, int groupId, IReadOnlyCollection<string> studentIds)
    {
        CourseGroup? group = await GroupWithPlacesAsync(organizationId, groupId);
        if (group == null)
        {
            return GroupNotFound;
        }
        if (studentIds.Count == 0)
        {
            return ServiceResult.Fail("Tick the students to add.");
        }

        var students = (await memberships.GetActivePeopleAsync(organizationId, MembershipRole.Student))
            .Where(s => studentIds.Contains(s.Id)).ToList();
        if (students.Count != studentIds.Distinct().Count())
        {
            return NotAStudent;
        }
        if (students.FirstOrDefault(s => s.Id == group.TeacherId) is ApplicationUser teacher)
        {
            return TeachesGroup(teacher.FullName, group);
        }

        var alreadyTaking = (await db.Enrolments.Where(e => e.CourseId == group.CourseId).Select(e => e.StudentId).ToListAsync()).ToHashSet();
        var toAdd = students.Where(s => !alreadyTaking.Contains(s.Id)).ToList();
        int free = group.Capacity - group.Enrolments.Count;
        if (toAdd.Count > free)
        {
            return ServiceResult.Fail($"Group {group.Name} has {DisplayNames.Plural(free, "free place")}; " +
                                      $"you chose {toAdd.Count} new students. Raise its places or choose fewer.");
        }

        foreach (ApplicationUser student in toAdd)
        {
            db.Enrolments.Add(NewEnrolment(organizationId, student.Id, group, bundleId: null));
        }
        await db.SaveChangesAsync();

        int skipped = students.Count - toAdd.Count;
        return ServiceResult.Ok($"{DisplayNames.Plural(toAdd.Count, "student")} added to {group.Course!.Name}, group {group.Name}." +
            (skipped > 0 ? $" {DisplayNames.Plural(skipped, "student")} already took the course and stayed in their group." : ""));
    }

    // Enrols the student in every course of the bundle they don't already take. For each course it uses the group
    // with the preferred name (e.g. "A", so a cohort stays together) if it has room, otherwise the group with the
    // most free places. Courses with no room are listed, not silently skipped.
    public async Task<ServiceResult> EnrolInBundleAsync(int organizationId, string studentId, int bundleId, string? preferredGroup)
    {
        Bundle? bundle = await db.Bundles
            .Include(b => b.Courses).ThenInclude(l => l.Course).ThenInclude(c => c!.Groups).ThenInclude(g => g.Enrolments)
            .FirstOrDefaultAsync(b => b.Id == bundleId && b.OrganizationId == organizationId);
        if (bundle == null)
        {
            return ServiceResult.Fail("We couldn't find that bundle.");
        }
        if (await StudentNameAsync(organizationId, studentId) is not string name)
        {
            return NotAStudent;
        }

        var taking = (await db.Enrolments.Where(e => e.StudentId == studentId).Select(e => e.CourseId).ToListAsync()).ToHashSet();
        var enrolled = new List<string>();
        var already = new List<string>();
        var noRoom = new List<string>();

        foreach (Course course in bundle.Courses.Select(l => l.Course!).OrderBy(c => c.Name))
        {
            if (taking.Contains(course.Id))
            {
                already.Add(course.Name);
                continue;
            }

            // Never the group the student teaches: they can't be on both sides of the same class.
            var open = course.Groups.Where(g => g.Enrolments.Count < g.Capacity && g.TeacherId != studentId).ToList();
            CourseGroup? group =
                open.FirstOrDefault(g => string.Equals(g.Name, preferredGroup?.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? open.OrderByDescending(g => g.Capacity - g.Enrolments.Count).ThenBy(g => g.Name).FirstOrDefault();
            if (group == null)
            {
                noRoom.Add(course.Name);
                continue;
            }

            Enrolment enrolment = NewEnrolment(organizationId, studentId, group, bundle.Id);
            db.Enrolments.Add(enrolment);
            group.Enrolments.Add(enrolment);
            enrolled.Add($"{course.Name} ({group.Name})");
        }

        if (enrolled.Count == 0)
        {
            return ServiceResult.Fail($"{name} wasn't enrolled in anything from {bundle.Name}." + Details(already, noRoom));
        }

        await db.SaveChangesAsync();
        return ServiceResult.Ok($"{name} enrolled through {bundle.Name} in {string.Join(", ", enrolled)}." + Details(already, noRoom));
    }

    public async Task<ServiceResult> UnenrolAsync(int organizationId, int enrolmentId)
    {
        Enrolment? enrolment = await db.Enrolments.Include(e => e.Student).Include(e => e.Course).Include(e => e.Group)
            .FirstOrDefaultAsync(e => e.Id == enrolmentId && e.OrganizationId == organizationId);
        if (enrolment == null)
        {
            return ServiceResult.Fail("We couldn't find that enrolment.");
        }

        db.Enrolments.Remove(enrolment);
        await db.SaveChangesAsync();
        return ServiceResult.Ok($"{enrolment.Student!.FullName} is no longer enrolled in {enrolment.Course!.Name} (group {enrolment.Group!.Name}).");
    }

    private static string Details(List<string> already, List<string> noRoom) =>
        (already.Count > 0 ? $" Already taking: {string.Join(", ", already)}." : "") +
        (noRoom.Count > 0 ? $" No group with free places: {string.Join(", ", noRoom)}." : "");

    private Enrolment NewEnrolment(int organizationId, string studentId, CourseGroup group, int? bundleId) => new()
    {
        OrganizationId = organizationId,
        StudentId = studentId,
        CourseId = group.CourseId,
        GroupId = group.Id,
        BundleId = bundleId,
        EnrolledAt = clock.GetUtcNow().UtcDateTime,
    };

    private Task<CourseGroup?> GroupWithPlacesAsync(int organizationId, int groupId) =>
        db.CourseGroups.Include(g => g.Course).ThenInclude(c => c!.Groups).Include(g => g.Enrolments)
            .FirstOrDefaultAsync(g => g.Id == groupId && g.OrganizationId == organizationId);

    // Only approved students of this organisation can be enrolled.
    private async Task<string?> StudentNameAsync(int organizationId, string studentId) =>
        await memberships.HasActiveRoleAsync(studentId, organizationId, MembershipRole.Student)
            ? (await db.Users.FindAsync(studentId))?.FullName
            : null;

    private IQueryable<Enrolment> EnrolmentsOf(int organizationId) =>
        db.Enrolments.AsNoTracking().Where(e => e.OrganizationId == organizationId)
            .Include(e => e.Course).Include(e => e.Group).ThenInclude(g => g!.Teacher).Include(e => e.Bundle);

    private static List<Enrolment> Sorted(IEnumerable<Enrolment> enrolments) =>
        enrolments.OrderBy(e => e.Course!.Name).ToList();

    private static ServiceResult Full(CourseGroup group) =>
        ServiceResult.Fail($"Group {group.Name} of {group.Course!.Name} is full ({group.Capacity} places). Choose another group or raise its places.");

    // Someone who teaches a group can't also be one of its students (a clash by definition once it's scheduled).
    // "Choose another group" only when the course has one; otherwise say what would make it possible.
    private static ServiceResult TeachesGroup(string name, CourseGroup group) =>
        ServiceResult.Fail($"{name} teaches group {group.Name} of {group.Course!.Name}, so can't also be a student in it. " +
                           (group.Course.Groups.Count > 1
                               ? "Choose another group."
                               : "Add another group to the course, or give this one a different teacher."));

    private static readonly ServiceResult GroupNotFound = ServiceResult.Fail("We couldn't find that group.");
    private static readonly ServiceResult NotAStudent = ServiceResult.Fail("Only approved students of this organisation can be enrolled.");
}
