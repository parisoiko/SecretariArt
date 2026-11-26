namespace SecretariArt.Models;

// Something the organisation teaches (e.g. "Acting I", "Ballet, beginners"), taught in one or more groups.
public class Course
{
    public int Id { get; set; }
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    public string Name { get; set; } = "";
    public string? Description { get; set; }

    // Each group meets this many times a week, for this long each time.
    public int SessionMinutes { get; set; }
    public int SessionsPerWeek { get; set; }

    public RoomFeatures RequiredFeatures { get; set; }

    // Teachers who can teach it. A group's teacher is chosen from them.
    public List<CourseTeacher> Teachers { get; set; } = [];
    public List<CourseGroup> Groups { get; set; } = [];
    public List<BundleCourse> Bundles { get; set; } = [];
}

public class CourseTeacher
{
    public int CourseId { get; set; }
    public Course? Course { get; set; }

    public string TeacherId { get; set; } = "";
    public ApplicationUser? Teacher { get; set; }
}

// One class of a course (e.g. "A", "Evening"): its own students, teacher and, later, its own sessions in the schedule.
public class CourseGroup
{
    public int Id { get; set; }
    public int OrganizationId { get; set; }

    public int CourseId { get; set; }
    public Course? Course { get; set; }

    public string Name { get; set; } = "";
    public int Capacity { get; set; }

    public string? TeacherId { get; set; }
    public ApplicationUser? Teacher { get; set; }

    public List<Enrolment> Enrolments { get; set; } = [];
    public List<ClassSession> Sessions { get; set; } = [];
}

// A set of courses taken together (e.g. "Year 1", "Full-time foundation"), so a student is enrolled in all at once.
public class Bundle
{
    public int Id { get; set; }
    public int OrganizationId { get; set; }
    public Organization? Organization { get; set; }

    public string Name { get; set; } = "";
    public string? Description { get; set; }

    public List<BundleCourse> Courses { get; set; } = [];
}

public class BundleCourse
{
    public int BundleId { get; set; }
    public Bundle? Bundle { get; set; }

    public int CourseId { get; set; }
    public Course? Course { get; set; }
}

// A student's place in one group of a course. A student takes a course in only one group at a time.
public class Enrolment
{
    public int Id { get; set; }
    public int OrganizationId { get; set; }

    public string StudentId { get; set; } = "";
    public ApplicationUser? Student { get; set; }

    // Kept next to GroupId so "one group per course" can be a unique index.
    public int CourseId { get; set; }
    public Course? Course { get; set; }

    public int GroupId { get; set; }
    public CourseGroup? Group { get; set; }

    // Set when the place came from enrolling in a bundle (shown, and used for bundle prices in milestone 4).
    public int? BundleId { get; set; }
    public Bundle? Bundle { get; set; }

    public DateTime EnrolledAt { get; set; }
}
