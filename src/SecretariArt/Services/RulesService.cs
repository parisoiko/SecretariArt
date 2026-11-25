using Microsoft.EntityFrameworkCore;
using SecretariArt.Data;
using SecretariArt.Models;

namespace SecretariArt.Services;

public record RulesInput(
    TeachingDays TeachingDays, TimeOnly DayStarts, TimeOnly DayEnds, int SlotMinutes,
    int MinSessionMinutes, int MaxSessionMinutes, int MinSessionsPerWeek, int MaxSessionsPerWeek,
    int BreakBetweenSessionsMinutes, int? MaxTeachingHoursPerDay, DateOnly? TermStarts, DateOnly? TermEnds);

public interface IRulesService
{
    // The saved rules, or the defaults (not yet saved) for an organisation that hasn't set any.
    Task<OrganizationRules> GetAsync(int organizationId);
    Task<bool> HasSavedAsync(int organizationId);
    Task<ServiceResult> SaveAsync(int organizationId, RulesInput input);
}

public class RulesService(SecretariArtDbContext db, TimeProvider clock) : IRulesService
{
    public async Task<OrganizationRules> GetAsync(int organizationId) =>
        await db.OrganizationRules.AsNoTracking().FirstOrDefaultAsync(r => r.OrganizationId == organizationId)
        ?? new OrganizationRules { OrganizationId = organizationId };

    public Task<bool> HasSavedAsync(int organizationId) =>
        db.OrganizationRules.AnyAsync(r => r.OrganizationId == organizationId);

    public async Task<ServiceResult> SaveAsync(int organizationId, RulesInput input)
    {
        if (Validate(input) is string problem)
        {
            return ServiceResult.Fail(problem);
        }

        OrganizationRules? rules = await db.OrganizationRules.FirstOrDefaultAsync(r => r.OrganizationId == organizationId);
        if (rules == null)
        {
            rules = new OrganizationRules { OrganizationId = organizationId };
            db.OrganizationRules.Add(rules);
        }

        rules.TeachingDays = input.TeachingDays;
        rules.DayStarts = input.DayStarts;
        rules.DayEnds = input.DayEnds;
        rules.SlotMinutes = input.SlotMinutes;
        rules.MinSessionMinutes = input.MinSessionMinutes;
        rules.MaxSessionMinutes = input.MaxSessionMinutes;
        rules.MinSessionsPerWeek = input.MinSessionsPerWeek;
        rules.MaxSessionsPerWeek = input.MaxSessionsPerWeek;
        rules.BreakBetweenSessionsMinutes = input.BreakBetweenSessionsMinutes;
        rules.MaxTeachingHoursPerDay = input.MaxTeachingHoursPerDay;
        rules.TermStarts = input.TermStarts;
        rules.TermEnds = input.TermEnds;
        rules.UpdatedAt = clock.GetUtcNow().UtcDateTime;
        await db.SaveChangesAsync();

        // Saving is allowed even if existing courses no longer fit (the admin may be tightening the rules on
        // purpose), but they must hear about it: the courses page flags each one.
        var courses = await db.Courses.AsNoTracking().Where(c => c.OrganizationId == organizationId).ToListAsync();
        var misfits = courses.Where(c => CourseRules.Problems(c.SessionMinutes, c.SessionsPerWeek, rules).Any())
            .Select(c => c.Name).ToList();
        return ServiceResult.Ok(misfits.Count == 0
            ? "Rules saved."
            : $"Rules saved. {misfits.Count} course{(misfits.Count == 1 ? " no longer fits" : "s no longer fit")} them: " +
              $"{string.Join(", ", misfits)}. They're marked on the Courses tab.");
    }

    private static string? Validate(RulesInput input)
    {
        if (input.TeachingDays == TeachingDays.None)
        {
            return "Choose at least one teaching day.";
        }
        if (input.DayEnds <= input.DayStarts)
        {
            return "The day must end after it starts.";
        }
        if (!OrganizationRulesDefaults.SlotChoices.Contains(input.SlotMinutes))
        {
            return "Choose one of the start-time steps offered.";
        }
        if (input.MinSessionMinutes > input.MaxSessionMinutes)
        {
            return "The shortest session can't be longer than the longest.";
        }
        if (input.MinSessionMinutes % input.SlotMinutes != 0 || input.MaxSessionMinutes % input.SlotMinutes != 0)
        {
            return $"Session lengths must be multiples of the {input.SlotMinutes}-minute step.";
        }
        if (input.MaxSessionMinutes > (input.DayEnds - input.DayStarts).TotalMinutes)
        {
            return "The longest session doesn't fit in the teaching day.";
        }
        if (input.MinSessionsPerWeek > input.MaxSessionsPerWeek)
        {
            return "The minimum sessions per week can't be more than the maximum.";
        }
        if (input.TermStarts is DateOnly starts && input.TermEnds is DateOnly ends && ends <= starts)
        {
            return "The term must end after it starts.";
        }
        if (input.TermStarts.HasValue != input.TermEnds.HasValue)
        {
            return "Give both term dates, or neither.";
        }
        return null;
    }
}

// Whether a course's length and frequency fit the organisation's rules. Used when saving a course (refused) and when
// listing courses after the rules changed (flagged).
public static class CourseRules
{
    public static IEnumerable<string> Problems(int sessionMinutes, int sessionsPerWeek, OrganizationRules rules)
    {
        if (sessionMinutes < rules.MinSessionMinutes || sessionMinutes > rules.MaxSessionMinutes)
        {
            yield return $"Sessions must last between {DisplayNames.Minutes(rules.MinSessionMinutes)} and " +
                         $"{DisplayNames.Minutes(rules.MaxSessionMinutes)} (see Rules).";
        }
        else if (sessionMinutes % rules.SlotMinutes != 0)
        {
            yield return $"Session length must be a multiple of {rules.SlotMinutes} minutes (see Rules).";
        }

        if (sessionsPerWeek < rules.MinSessionsPerWeek || sessionsPerWeek > rules.MaxSessionsPerWeek)
        {
            yield return $"Sessions per week must be between {rules.MinSessionsPerWeek} and {rules.MaxSessionsPerWeek} (see Rules).";
        }
    }
}
