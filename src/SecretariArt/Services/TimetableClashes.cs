using Microsoft.EntityFrameworkCore;
using SecretariArt.Data;
using SecretariArt.Models;

namespace SecretariArt.Services;

// Clashes created by changes outside the schedule editor: enrolling a student in a group whose classes overlap
// their other classes, or giving a group a teacher who already teaches at those times. Same rules as the editor
// (ScheduleChecker): within an organisation a student clash is refused; teaching twice at once is refused anywhere.
public static class TimetableClashes
{
    // The student's classes in this organisation that overlap the group's, ignoring the course itself (a move
    // between groups of one course replaces that course's classes). extraGroupIds: groups being added in the same
    // operation (a bundle), not saved yet.
    public static async Task<string?> ForStudentAsync(
        SecretariArtDbContext db, int organizationId, string studentId, CourseGroup group, IEnumerable<int>? extraGroupIds = null)
    {
        var newSessions = await SessionsOf(db, [group.Id]);
        if (newSessions.Count == 0)
        {
            return null;
        }

        var groupIds = await db.Enrolments
            .Where(e => e.OrganizationId == organizationId && e.StudentId == studentId && e.CourseId != group.CourseId)
            .Select(e => e.GroupId).ToListAsync();
        groupIds.AddRange(extraGroupIds ?? []);

        ClassSession? clash = (await SessionsOf(db, groupIds))
            .FirstOrDefault(s => newSessions.Any(n => n.Overlaps(s.Day, s.Starts, s.Ends)));
        return clash == null ? null : $"{clash.Group!.Course!.Name} (group {clash.Group.Name}) on {clash.Day} {clash.Starts:HH:mm}–{clash.Ends:HH:mm}";
    }

    // Another class the teacher teaches (in any organisation, in the same weeks) at the same time as the group's.
    public static async Task<string?> ForTeacherAsync(SecretariArtDbContext db, string teacherId, CourseGroup group)
    {
        var groupSessions = await SessionsOf(db, [group.Id]);
        if (groupSessions.Count == 0)
        {
            return null;
        }

        var taught = await db.ClassSessions.AsNoTracking()
            .Include(s => s.Group).ThenInclude(g => g!.Course)
            .Where(s => s.Group!.TeacherId == teacherId && s.GroupId != group.Id)
            .ToListAsync();
        var organizationIds = taught.Select(s => s.OrganizationId).Append(group.OrganizationId).Distinct().ToList();
        var rules = await db.OrganizationRules.AsNoTracking().Where(r => organizationIds.Contains(r.OrganizationId))
            .ToDictionaryAsync(r => r.OrganizationId);

        ClassSession? clash = taught.FirstOrDefault(t => groupSessions.Any(s => s.Overlaps(t.Day, t.Starts, t.Ends)) &&
            ScheduleChecker.TermsOverlap(rules.GetValueOrDefault(group.OrganizationId), rules.GetValueOrDefault(t.OrganizationId)));
        if (clash == null)
        {
            return null;
        }
        return clash.OrganizationId == group.OrganizationId
            ? $"{clash.Group!.Course!.Name} (group {clash.Group.Name}) on {clash.Day} {clash.Starts:HH:mm}–{clash.Ends:HH:mm}"
            : $"a class at another organisation on {clash.Day} {clash.Starts:HH:mm}–{clash.Ends:HH:mm}";
    }

    private static Task<List<ClassSession>> SessionsOf(SecretariArtDbContext db, IEnumerable<int> groupIds)
    {
        var ids = groupIds.Distinct().ToList();
        return db.ClassSessions.AsNoTracking().Include(s => s.Group).ThenInclude(g => g!.Course)
            .Where(s => ids.Contains(s.GroupId)).ToListAsync();
    }
}
