namespace SecretariArt.Models;

// The organisation's timetabling rules: courses are checked against them, and the schedule editor and the generator
// place classes within them. One row per organisation, created on first save.
public class OrganizationRules
{
    public int Id { get; set; }
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    public TeachingDays TeachingDays { get; set; } = OrganizationRulesDefaults.TeachingDays;
    public TimeOnly DayStarts { get; set; } = OrganizationRulesDefaults.DayStarts;
    public TimeOnly DayEnds { get; set; } = OrganizationRulesDefaults.DayEnds;

    // Sessions start on this grid (e.g. every 15 minutes) and their lengths are multiples of it.
    public int SlotMinutes { get; set; } = OrganizationRulesDefaults.SlotMinutes;

    public int MinSessionMinutes { get; set; } = OrganizationRulesDefaults.MinSessionMinutes;
    public int MaxSessionMinutes { get; set; } = OrganizationRulesDefaults.MaxSessionMinutes;
    public int MinSessionsPerWeek { get; set; } = OrganizationRulesDefaults.MinSessionsPerWeek;
    public int MaxSessionsPerWeek { get; set; } = OrganizationRulesDefaults.MaxSessionsPerWeek;

    // Changeover time a room needs between two sessions (setting up, airing, moving pianos).
    public int BreakBetweenSessionsMinutes { get; set; } = OrganizationRulesDefaults.BreakBetweenSessionsMinutes;

    // Optional limit on a teacher's teaching hours in one day.
    public int? MaxTeachingHoursPerDay { get; set; }

    // The period the schedule covers (a term or semester). Optional until schedules exist.
    public DateOnly? TermStarts { get; set; }
    public DateOnly? TermEnds { get; set; }

    public DateTime UpdatedAt { get; set; }
}

public static class TermExtensions
{
    // No term dates means "always": an organisation that hasn't set a term isn't limited by one. Null rules too.
    public static bool InTerm(this OrganizationRules? rules, DateOnly date) =>
        (rules?.TermStarts == null || date >= rules.TermStarts) && (rules?.TermEnds == null || date <= rules.TermEnds);

    // Whether the term runs on any day of this month.
    public static bool TermRunsIn(this OrganizationRules? rules, DateOnly month) =>
        (rules?.TermStarts == null || rules.TermStarts <= month.AddMonths(1).AddDays(-1)) && (rules?.TermEnds == null || rules.TermEnds >= month);
}

// Sensible defaults for an arts school until the admin saves their own.
public static class OrganizationRulesDefaults
{
    public const TeachingDays TeachingDays = Models.TeachingDays.Monday | Models.TeachingDays.Tuesday |
        Models.TeachingDays.Wednesday | Models.TeachingDays.Thursday | Models.TeachingDays.Friday | Models.TeachingDays.Saturday;
    public static readonly TimeOnly DayStarts = new(9, 0);
    public static readonly TimeOnly DayEnds = new(22, 0);
    public const int SlotMinutes = 15;
    public const int MinSessionMinutes = 45;
    public const int MaxSessionMinutes = 180;
    public const int MinSessionsPerWeek = 1;
    public const int MaxSessionsPerWeek = 5;
    public const int BreakBetweenSessionsMinutes = 0;

    public static readonly int[] SlotChoices = [5, 10, 15, 30, 60];
}

[Flags]
public enum TeachingDays
{
    None = 0,
    Monday = 1,
    Tuesday = 2,
    Wednesday = 4,
    Thursday = 8,
    Friday = 16,
    Saturday = 32,
    Sunday = 64,
}
