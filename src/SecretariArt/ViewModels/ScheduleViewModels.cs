using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using SecretariArt.Models;
using SecretariArt.Services;

namespace SecretariArt.ViewModels;

// One block in a week calendar. Tone picks the colour (an organisation, or a kind of class); Alert marks a clash in
// red and Note something to check in amber; both also say it in words, never by colour alone. Compact is the one detail
// line a block under 90 minutes has room for (default: Line2).
public record WeekBlock(
    DayOfWeek Day, TimeOnly Starts, TimeOnly Ends, string Title, string? Line2, string? Line3, string? Url,
    int Tone = 0, string? Alert = null, string? Note = null, string? Compact = null)
{
    // Its colour, and red for a clash or amber for a note: the classes shared by the grid and the agenda list.
    public string StateClasses => DisplayNames.ToneClass(Tone) + (Alert != null ? " clash" : Note != null ? " note" : "");

    // Shorter blocks show less text (site.css).
    public string SizeClass => (Ends - Starts).TotalMinutes switch { < 60 => " short", < 90 => " medium", < 120 => " compact", _ => "" };

    // Everything about the block, for its tooltip: the block itself may be too small to show it all.
    public string Tooltip => $"{Title}, {DisplayNames.TimeRange(Day, Starts, Ends)}" +
        (Line2 != null ? $", {Line2}" : "") + (Line3 != null ? $", {Line3}" : "") +
        (Alert != null ? $". {Alert}" : "") + (Note != null ? $". {Note}" : "");
}

public class WeekGridModel
{
    public required IReadOnlyList<DayOfWeek> Days { get; init; }
    public required TimeOnly From { get; init; }
    public required TimeOnly To { get; init; }
    public required IReadOnlyList<WeekBlock> Blocks { get; init; }
    public required string Label { get; init; }
    public string EmptyText { get; set; } = "Nothing this week.";

    // A grid that fits every block, rounded out to whole hours, at least the given day.
    public static WeekGridModel Fit(IReadOnlyList<WeekBlock> blocks, IEnumerable<DayOfWeek> days, TimeOnly from, TimeOnly to, string label)
    {
        var allDays = days.Union(blocks.Select(b => b.Day)).OrderBy(d => d.WeekIndex()).ToList();
        TimeOnly first = blocks.Select(b => b.Starts).Append(from).Min();
        TimeOnly last = blocks.Select(b => b.Ends).Append(to).Max();
        return new WeekGridModel
        {
            Days = allDays.Count == 0 ? DisplayNames.Week[..5] : allDays,
            From = new TimeOnly(first.Hour, 0),
            To = last.Minute == 0 || last.Hour == 23 ? last : new TimeOnly(last.Hour + 1, 0),
            Blocks = blocks,
            Label = label,
        };
    }

    // Side-by-side columns for blocks that overlap on the same day: (lane, lanes in that day).
    public IReadOnlyList<(WeekBlock Block, int Lane, int Lanes)> Lanes()
    {
        var result = new List<(WeekBlock, int, int)>();
        foreach (var day in Blocks.GroupBy(b => b.Day))
        {
            // Lanes are counted per cluster of overlapping blocks, not per day: a class that overlaps nothing gets
            // the full width even if two other classes that day share theirs (QA, S7).
            var laneEnds = new List<TimeOnly>();
            var cluster = new List<(WeekBlock Block, int Lane)>();
            TimeOnly clusterEnds = TimeOnly.MinValue;

            void Flush()
            {
                result.AddRange(cluster.Select(p => (p.Block, p.Lane, laneEnds.Count)));
                cluster.Clear();
                laneEnds.Clear();
            }

            foreach (WeekBlock block in day.OrderBy(b => b.Starts).ThenBy(b => b.Ends))
            {
                if (cluster.Count > 0 && block.Starts >= clusterEnds)
                {
                    Flush();
                }

                int lane = laneEnds.FindIndex(end => end <= block.Starts);
                if (lane < 0)
                {
                    laneEnds.Add(block.Ends);
                    lane = laneEnds.Count - 1;
                }
                else
                {
                    laneEnds[lane] = block.Ends;
                }
                cluster.Add((block, lane));
                clusterEnds = cluster.Count == 1 || block.Ends > clusterEnds ? block.Ends : clusterEnds;
            }
            Flush();
        }
        return result;
    }
}

// The admin's Schedule tab.
public class ScheduleViewModel
{
    public required OrganizationSchedule Schedule { get; init; }
    public required WeekGridModel Grid { get; init; }
    public required SessionFormViewModel Form { get; init; }

    // What the grid shows: everything, or one room, teacher or group.
    public string? View { get; init; }
    public int? RoomId { get; init; }
    public string? TeacherId { get; init; }
    public int? GroupId { get; init; }

    public IReadOnlyList<Suggestion>? Suggestions { get; init; }
    public CourseGroup? SuggestionsFor { get; init; }

    public int ProblemCount => Schedule.Sessions.Count(s => s.Problems.Count > 0);
    public int WarningCount => Schedule.Sessions.Count(s => s.Problems.Count == 0 && s.Warnings.Count > 0);
}

public class SessionFormViewModel
{
    [Required(ErrorMessage = "Choose the group.")]
    [Display(Name = "Group")]
    public int? GroupId { get; set; }

    [Required(ErrorMessage = "Choose the day.")]
    [Display(Name = "Day")]
    public DayOfWeek? Day { get; set; }

    [Required(ErrorMessage = "Enter the start time.")]
    [Display(Name = "Starts at")]
    public TimeOnly? Starts { get; set; }

    [Required(ErrorMessage = "Choose the classroom.")]
    [Display(Name = "Classroom")]
    public int? ClassroomId { get; set; }

    [ValidateNever] public int? SessionId { get; set; }
    [ValidateNever] public IReadOnlyList<CourseGroup> Groups { get; set; } = [];
    [ValidateNever] public IReadOnlyList<Classroom> Rooms { get; set; } = [];
    [ValidateNever] public OrganizationRules Rules { get; set; } = new();

    // When editing: what's wrong with the session as it stands.
    [ValidateNever] public SessionView? Current { get; set; }

    public SessionInput ToInput() => new(GroupId!.Value, ClassroomId!.Value, Day!.Value, Starts!.Value);
}

// The personal Schedule page (students and teachers).
// The tabs of My schedule (the ?tab= value).
public static class ScheduleTabs
{
    public const string Week = "week";
    public const string Availability = "availability";
    public const string Book = "book";
    public const string CheckIns = "check-ins";
}

// A record, so the controller can fill what every tab shares once and add each tab's own data with `with`.
public record PersonalScheduleViewModel
{
    public required string Tab { get; init; }
    public required UserRoles Roles { get; init; }
    public WeekGridModel? Grid { get; init; }
    public IReadOnlyList<PersonalSession> Sessions { get; init; } = [];
    public IReadOnlyList<TeachingPlace> Places { get; init; } = [];

    // Organisation id → colour, the same on every tab.
    public IReadOnlyDictionary<int, int> Tones { get; init; } = new Dictionary<int, int>();
    public IReadOnlyList<Organization> Organizations { get; init; } = [];

    // Per-class courses: classes the student can check in to, or the teacher's classes with check-ins.
    // Each tab shows only when there is something in it.
    public IReadOnlyList<ClassDate> Classes { get; init; } = [];
    public bool CanBook { get; init; }
    public bool HasCheckIns { get; init; }
    public IReadOnlyDictionary<int, PaymentSettings> CancelRules { get; init; } = new Dictionary<int, PaymentSettings>();
}

public class AvailabilityFormViewModel
{
    [Required(ErrorMessage = "Choose the organisation.")]
    public int? OrganizationId { get; set; }

    [Required(ErrorMessage = "Choose the day.")]
    public DayOfWeek? Day { get; set; }

    [Required(ErrorMessage = "Enter the start time.")]
    public TimeOnly? From { get; set; }

    [Required(ErrorMessage = "Enter the end time.")]
    public TimeOnly? To { get; set; }

    public AvailabilityInput ToInput() => new(OrganizationId!.Value, Day!.Value, From!.Value, To!.Value);
}

// The "Generate a timetable" page.
public class GenerateViewModel
{
    public required OrganizationSchedule Schedule { get; init; }
    public bool HasDraft { get; init; }
    public GenerationMode Mode { get; init; }
}
