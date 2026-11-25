using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding.Validation;
using SecretariArt.Models;
using SecretariArt.Services;

namespace SecretariArt.ViewModels;

public class RulesViewModel : IValidatableObject
{
    [Required(ErrorMessage = "Choose at least one teaching day."), MinLength(1, ErrorMessage = "Choose at least one teaching day."), Display(Name = "Teaching days")]
    public List<TeachingDays> Days { get; set; } = [];

    [Required(ErrorMessage = "Enter when the teaching day starts."), Display(Name = "Day starts")]
    public TimeOnly? DayStarts { get; set; }

    [Required(ErrorMessage = "Enter when the teaching day ends."), Display(Name = "Day ends")]
    public TimeOnly? DayEnds { get; set; }

    [Required(ErrorMessage = "Choose the start-time step."), Display(Name = "Sessions start every")]
    public int? SlotMinutes { get; set; }

    [Required(ErrorMessage = "Enter the shortest session length."), Range(5, 600, ErrorMessage = "Enter between 5 and 600 minutes.")]
    [Display(Name = "Shortest session (minutes)")]
    public int? MinSessionMinutes { get; set; }

    [Required(ErrorMessage = "Enter the longest session length."), Range(5, 600, ErrorMessage = "Enter between 5 and 600 minutes.")]
    [Display(Name = "Longest session (minutes)")]
    public int? MaxSessionMinutes { get; set; }

    [Required(ErrorMessage = "Enter the fewest sessions a course can have per week."), Range(1, 14, ErrorMessage = "Enter between 1 and 14.")]
    [Display(Name = "Fewest sessions per week")]
    public int? MinSessionsPerWeek { get; set; }

    [Required(ErrorMessage = "Enter the most sessions a course can have per week."), Range(1, 14, ErrorMessage = "Enter between 1 and 14.")]
    [Display(Name = "Most sessions per week")]
    public int? MaxSessionsPerWeek { get; set; }

    [Required(ErrorMessage = "Enter the changeover time (0 for none)."), Range(0, 120, ErrorMessage = "Enter between 0 and 120 minutes.")]
    [Display(Name = "Changeover between sessions in a room (minutes)")]
    public int? BreakBetweenSessionsMinutes { get; set; }

    [Range(1, 16, ErrorMessage = "Enter between 1 and 16 hours, or leave it empty.")]
    [Display(Name = "Most teaching hours per teacher per day (optional)")]
    public int? MaxTeachingHoursPerDay { get; set; }

    [Display(Name = "Term starts (optional)")]
    public DateOnly? TermStarts { get; set; }

    [Display(Name = "Term ends (optional)")]
    public DateOnly? TermEnds { get; set; }

    [ValidateNever] public bool Saved { get; set; }
    [ValidateNever] public DateTime? UpdatedAt { get; set; }

    public static RulesViewModel From(OrganizationRules rules, bool saved) => new()
    {
        Days = DisplayNames.AllDays.Where(d => rules.TeachingDays.HasFlag(d)).ToList(),
        DayStarts = rules.DayStarts,
        DayEnds = rules.DayEnds,
        SlotMinutes = rules.SlotMinutes,
        MinSessionMinutes = rules.MinSessionMinutes,
        MaxSessionMinutes = rules.MaxSessionMinutes,
        MinSessionsPerWeek = rules.MinSessionsPerWeek,
        MaxSessionsPerWeek = rules.MaxSessionsPerWeek,
        BreakBetweenSessionsMinutes = rules.BreakBetweenSessionsMinutes,
        MaxTeachingHoursPerDay = rules.MaxTeachingHoursPerDay,
        TermStarts = rules.TermStarts,
        TermEnds = rules.TermEnds,
        Saved = saved,
        UpdatedAt = saved ? rules.UpdatedAt : null,
    };

    public RulesInput ToInput() => new(
        Days.Aggregate(TeachingDays.None, (all, d) => all | d), DayStarts!.Value, DayEnds!.Value, SlotMinutes!.Value,
        MinSessionMinutes!.Value, MaxSessionMinutes!.Value, MinSessionsPerWeek!.Value, MaxSessionsPerWeek!.Value,
        BreakBetweenSessionsMinutes!.Value, MaxTeachingHoursPerDay, TermStarts, TermEnds);

    // The comparisons between fields, shown next to the field concerned (the service checks them again).
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (DayStarts.HasValue && DayEnds.HasValue && DayEnds <= DayStarts)
        {
            yield return new("The day must end after it starts.", [nameof(DayEnds)]);
        }
        if (MinSessionMinutes > MaxSessionMinutes)
        {
            yield return new("The longest session can't be shorter than the shortest.", [nameof(MaxSessionMinutes)]);
        }
        if (MinSessionsPerWeek > MaxSessionsPerWeek)
        {
            yield return new("The most can't be fewer than the fewest.", [nameof(MaxSessionsPerWeek)]);
        }
        if (TermStarts.HasValue != TermEnds.HasValue)
        {
            yield return new("Give both term dates, or neither.", [TermStarts.HasValue ? nameof(TermEnds) : nameof(TermStarts)]);
        }
        else if (TermEnds <= TermStarts)
        {
            yield return new("The term must end after it starts.", [nameof(TermEnds)]);
        }
    }
}

public class ClassroomViewModel
{
    [Required(ErrorMessage = "Give the classroom a name."), StringLength(100, ErrorMessage = "Keep the name under 100 characters.")]
    [Display(Name = "Name")]
    public string Name { get; set; } = "";

    [Required(ErrorMessage = "Enter how many people it holds."), Range(1, 1000, ErrorMessage = "Enter between 1 and 1000 people.")]
    [Display(Name = "Capacity (people)")]
    public int? Capacity { get; set; }

    [Required(ErrorMessage = "Choose the floor."), Display(Name = "Floor")]
    public FloorType? Floor { get; set; } = FloorType.Standard;

    [Display(Name = "Features")]
    public List<RoomFeatures> Features { get; set; } = [];

    [Display(Name = "Step-free access")]
    public bool StepFreeAccess { get; set; }

    [StringLength(500, ErrorMessage = "Keep the notes under 500 characters."), Display(Name = "Notes (optional)")]
    public string? Notes { get; set; }

    // Null when adding a new classroom.
    [ValidateNever] public int? Id { get; set; }

    public static ClassroomViewModel From(Classroom classroom) => new()
    {
        Id = classroom.Id, Name = classroom.Name, Capacity = classroom.Capacity, Floor = classroom.Floor,
        Features = classroom.Features.Split().ToList(), StepFreeAccess = classroom.StepFreeAccess, Notes = classroom.Notes,
    };

    public ClassroomInput ToInput() =>
        new(Name, Capacity!.Value, Floor!.Value, Combine(Features), StepFreeAccess, Notes);

    public static RoomFeatures Combine(IEnumerable<RoomFeatures> features) =>
        features.Aggregate(RoomFeatures.None, (all, f) => all | f);
}

public class CourseViewModel
{
    [Required(ErrorMessage = "Give the course a name."), StringLength(150, ErrorMessage = "Keep the name under 150 characters.")]
    [Display(Name = "Name")]
    public string Name { get; set; } = "";

    [StringLength(500, ErrorMessage = "Keep the description under 500 characters."), Display(Name = "Description (optional)")]
    public string? Description { get; set; }

    [Required(ErrorMessage = "Enter how long each session lasts."), Range(5, 600, ErrorMessage = "Enter between 5 and 600 minutes.")]
    [Display(Name = "Session length (minutes)")]
    public int? SessionMinutes { get; set; }

    [Required(ErrorMessage = "Enter how many sessions a week."), Range(1, 14, ErrorMessage = "Enter between 1 and 14.")]
    [Display(Name = "Sessions per week")]
    public int? SessionsPerWeek { get; set; }

    [Display(Name = "The room needs")]
    public List<RoomFeatures> RequiredFeatures { get; set; } = [];

    [Display(Name = "Teachers who can teach it")]
    public List<string> TeacherIds { get; set; } = [];

    [ValidateNever] public int? Id { get; set; }
    [ValidateNever] public IReadOnlyList<ApplicationUser> AvailableTeachers { get; set; } = [];
    [ValidateNever] public OrganizationRules Rules { get; set; } = new();

    public static CourseViewModel From(Course course) => new()
    {
        Id = course.Id, Name = course.Name, Description = course.Description, SessionMinutes = course.SessionMinutes,
        SessionsPerWeek = course.SessionsPerWeek, RequiredFeatures = course.RequiredFeatures.Split().ToList(),
        TeacherIds = course.Teachers.Select(t => t.TeacherId).ToList(),
    };

    public CourseInput ToInput() => new(Name, Description, SessionMinutes!.Value, SessionsPerWeek!.Value,
        ClassroomViewModel.Combine(RequiredFeatures), TeacherIds);
}

public class GroupViewModel
{
    [Required(ErrorMessage = "Give the group a name, e.g. A or Evening."), StringLength(50, ErrorMessage = "Keep the name under 50 characters.")]
    [Display(Name = "Group name")]
    public string Name { get; set; } = "";

    [Required(ErrorMessage = "Enter how many students it takes."), Range(1, 500, ErrorMessage = "Enter between 1 and 500 students.")]
    [Display(Name = "Places")]
    public int? Capacity { get; set; }

    [Display(Name = "Teacher")]
    public string? TeacherId { get; set; }

    [ValidateNever] public int? Id { get; set; }
    [ValidateNever] public Course Course { get; set; } = new();
    [ValidateNever] public IReadOnlyList<ApplicationUser> CourseTeachers { get; set; } = [];
    [ValidateNever] public int Enrolled { get; set; }

    public GroupInput ToInput() => new(Name, Capacity!.Value, string.IsNullOrEmpty(TeacherId) ? null : TeacherId);
}

public class CourseDetailsViewModel
{
    public required CourseOverview Overview { get; init; }

    // Approved students not yet taking the course, for "Add students" on each group.
    public required IReadOnlyList<ApplicationUser> StudentsNotTaking { get; init; }
}

public class BundleViewModel
{
    [Required(ErrorMessage = "Give the bundle a name, e.g. Year 1."), StringLength(150, ErrorMessage = "Keep the name under 150 characters.")]
    [Display(Name = "Name")]
    public string Name { get; set; } = "";

    [StringLength(500, ErrorMessage = "Keep the description under 500 characters."), Display(Name = "Description (optional)")]
    public string? Description { get; set; }

    [Required(ErrorMessage = "Choose at least one course."), MinLength(1, ErrorMessage = "Choose at least one course."), Display(Name = "Courses")]
    public List<int> CourseIds { get; set; } = [];

    [ValidateNever] public int? Id { get; set; }
    [ValidateNever] public IReadOnlyList<Course> AvailableCourses { get; set; } = [];

    public BundleInput ToInput() => new(Name, Description, CourseIds);
}

public class EnrolmentsViewModel
{
    public required IReadOnlyList<StudentEnrolments> Students { get; init; }
    public required int CourseCount { get; init; }
    public string? Search { get; init; }
}

public class StudentEnrolmentsViewModel
{
    public required StudentEnrolments Student { get; init; }
    public required IReadOnlyList<CourseOverview> Courses { get; init; }
    public required IReadOnlyList<Bundle> Bundles { get; init; }

    // Group names used in more than one course (e.g. "A"), offered as "keep them in group …" for bundles.
    public IEnumerable<string> SharedGroupNames => Courses
        .SelectMany(c => c.Course.Groups.Select(g => g.Name))
        .GroupBy(n => n, StringComparer.OrdinalIgnoreCase)
        .Where(g => g.Count() > 1).Select(g => g.Key).Order();
}

public class EnrolInGroupViewModel
{
    [Required(ErrorMessage = "Choose a course and group.")]
    public int? GroupId { get; set; }
}

public class EnrolInBundleViewModel
{
    [Required(ErrorMessage = "Choose a bundle.")]
    public int? BundleId { get; set; }

    public string? PreferredGroup { get; set; }
}

public class AddStudentsViewModel
{
    [Required(ErrorMessage = "Tick the students to add."), MinLength(1, ErrorMessage = "Tick the students to add.")]
    public List<string> StudentIds { get; set; } = [];
}
