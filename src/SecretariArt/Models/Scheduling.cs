namespace SecretariArt.Models;

// A weekly class in the timetable: one group of a course meets on this day, at this time, in this room, every week
// of the term. The teacher is the group's teacher (one source of truth: changing the group's teacher moves all its
// sessions with it).
public class ClassSession
{
    public int Id { get; set; }
    public int OrganizationId { get; set; }

    public int GroupId { get; set; }
    public CourseGroup? Group { get; set; }

    public int ClassroomId { get; set; }
    public Classroom? Classroom { get; set; }

    public DayOfWeek Day { get; set; }
    public TimeOnly Starts { get; set; }
    public TimeOnly Ends { get; set; }

    public DateTime UpdatedAt { get; set; }

    public bool Overlaps(DayOfWeek day, TimeOnly starts, TimeOnly ends, int gapMinutes = 0) =>
        Day == day && Starts < ends.AddMinutes(gapMinutes) && starts < Ends.AddMinutes(gapMinutes);
}

// When a teacher can teach at one organisation, week by week. Each teacher posts their own, per organisation.
public class TeacherAvailability
{
    public int Id { get; set; }
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    public string TeacherId { get; set; } = "";
    public ApplicationUser? Teacher { get; set; }

    public DayOfWeek Day { get; set; }
    public TimeOnly From { get; set; }
    public TimeOnly To { get; set; }

    public bool Covers(DayOfWeek day, TimeOnly starts, TimeOnly ends) => Day == day && From <= starts && ends <= To;
}

// A message to one person, e.g. "Your Acting I class moved to Tuesday 18:00". Shown under the bell in the navigation.
public class Notification
{
    public int Id { get; set; }
    public string UserId { get; set; } = "";
    public ApplicationUser? User { get; set; }

    public int? OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    public string Text { get; set; } = "";
    public string? Url { get; set; }

    public DateTime CreatedAt { get; set; }
    public DateTime? ReadAt { get; set; }
}
