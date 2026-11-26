using Microsoft.EntityFrameworkCore;
using SecretariArt.Data;
using SecretariArt.Models;

namespace SecretariArt.Services;

public record AvailabilityInput(int OrganizationId, DayOfWeek Day, TimeOnly From, TimeOnly To);

// A teacher's own weekly availability, per organisation they teach at.
public record TeachingPlace(Organization Organization, OrganizationRules Rules, IReadOnlyList<TeacherAvailability> Slots);

public interface IAvailabilityService
{
    Task<List<TeachingPlace>> GetForTeacherAsync(string teacherId);
    Task<ServiceResult> AddAsync(string teacherId, AvailabilityInput input);
    Task<ServiceResult> RemoveAsync(string teacherId, int availabilityId);
}

public class AvailabilityService(SecretariArtDbContext db, IMembershipService memberships, IRulesService rules) : IAvailabilityService
{
    public async Task<List<TeachingPlace>> GetForTeacherAsync(string teacherId)
    {
        var organizations = await db.Memberships.AsNoTracking()
            .Where(m => m.UserId == teacherId && m.Role == MembershipRole.Teacher && m.Status == MembershipStatus.Active &&
                        m.Organization!.Status == OrganizationStatus.Active)
            .Select(m => m.Organization!).OrderBy(o => o.Name).ToListAsync();
        var slots = await db.TeacherAvailability.AsNoTracking().Where(a => a.TeacherId == teacherId).ToListAsync();

        var places = new List<TeachingPlace>();
        foreach (Organization organization in organizations)
        {
            places.Add(new TeachingPlace(organization, await rules.GetAsync(organization.Id),
                slots.Where(s => s.OrganizationId == organization.Id)
                    .OrderBy(s => Array.IndexOf(DisplayNames.Week, s.Day)).ThenBy(s => s.From).ToList()));
        }
        return places;
    }

    // Overlapping or touching periods on the same day are merged, so "10–12" plus "12–14" reads as "10–14".
    public async Task<ServiceResult> AddAsync(string teacherId, AvailabilityInput input)
    {
        if (!await memberships.HasActiveRoleAsync(teacherId, input.OrganizationId, MembershipRole.Teacher))
        {
            return ServiceResult.Fail("You can post availability only where you're an approved teacher.");
        }
        if (input.To <= input.From)
        {
            return ServiceResult.Fail("The end time must be after the start time.");
        }

        OrganizationRules organizationRules = await rules.GetAsync(input.OrganizationId);
        var sameDay = await db.TeacherAvailability
            .Where(a => a.TeacherId == teacherId && a.OrganizationId == input.OrganizationId && a.Day == input.Day)
            .ToListAsync();
        var touching = sameDay.Where(a => a.From <= input.To && input.From <= a.To).ToList();

        TimeOnly from = touching.Select(a => a.From).Append(input.From).Min();
        TimeOnly to = touching.Select(a => a.To).Append(input.To).Max();
        db.TeacherAvailability.RemoveRange(touching);
        db.TeacherAvailability.Add(new TeacherAvailability
        {
            TeacherId = teacherId, OrganizationId = input.OrganizationId, Day = input.Day, From = from, To = to,
        });
        await db.SaveChangesAsync();

        string message = touching.Count > 0
            ? $"Saved: {input.Day} {from:HH:mm}–{to:HH:mm} (joined with what you'd already posted)."
            : $"Saved: {input.Day} {from:HH:mm}–{to:HH:mm}.";
        bool outside = !organizationRules.TeachingDays.HasFlag(input.Day.ToTeachingDay()) ||
                       from < organizationRules.DayStarts || to > organizationRules.DayEnds;
        return ServiceResult.Ok(outside
            ? $"{message} Note: classes there are only {organizationRules.TeachingDays.Display()}, {organizationRules.DayStarts:HH:mm}–{organizationRules.DayEnds:HH:mm}."
            : message);
    }

    public async Task<ServiceResult> RemoveAsync(string teacherId, int availabilityId)
    {
        TeacherAvailability? slot = await db.TeacherAvailability.FirstOrDefaultAsync(a => a.Id == availabilityId && a.TeacherId == teacherId);
        if (slot == null)
        {
            return ServiceResult.Fail("We couldn't find that time.");
        }

        db.TeacherAvailability.Remove(slot);
        await db.SaveChangesAsync();
        return ServiceResult.Ok($"Removed {slot.Day} {slot.From:HH:mm}–{slot.To:HH:mm}. Classes already there stay; the admin sees them flagged.");
    }
}
