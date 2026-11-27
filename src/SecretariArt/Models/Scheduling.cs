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

    // How long the class is. Worked out, not stored.
    public int Minutes => (int)(Ends - Starts).TotalMinutes;

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

// Milestone 5. A timetable worked out by the generator (TimetableGenerator), waiting for the admin to look at it and
// use it or throw it away. One per organisation: generating again replaces it.
public class ScheduleDraft
{
    public int Id { get; set; }
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    public GenerationMode Mode { get; set; }

    public string? CreatedById { get; set; }
    public DateTime CreatedAt { get; set; }

    // How the solver did: whether it proved nothing better exists, and how long it took.
    public bool Optimal { get; set; }
    public int Milliseconds { get; set; }

    public List<DraftSession> Sessions { get; set; } = [];
    public List<DraftGap> Gaps { get; set; } = [];
}

public enum GenerationMode
{
    // Keep every class already in the timetable; place only the sessions groups are missing.
    FillGaps,
    // Plan the whole week again; the current classes are replaced when the draft is used.
    StartOver,
}

// One weekly class the generator placed.
public class DraftSession
{
    public int Id { get; set; }
    public int DraftId { get; set; }
    public ScheduleDraft? Draft { get; set; }

    public int GroupId { get; set; }
    public int ClassroomId { get; set; }
    public DayOfWeek Day { get; set; }
    public TimeOnly Starts { get; set; }
    public TimeOnly Ends { get; set; }

    // "Stage Combat (group B)" when the draft was made, so a refusal can still name a group deleted since.
    public string GroupLabel { get; set; } = "";
}

// Sessions the generator couldn't place for a group, and why.
public class DraftGap
{
    public int Id { get; set; }
    public int DraftId { get; set; }
    public ScheduleDraft? Draft { get; set; }

    public int GroupId { get; set; }
    public int Missing { get; set; }
    public string Reason { get; set; } = "";
}
