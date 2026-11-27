using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SecretariArt.Data;
using SecretariArt.Models;
using SecretariArt.Services;
using SecretariArt.ViewModels;

namespace SecretariArt.Tests.Services;

// Milestone 3: the timetable's conflict checks, suggestions, notifications, availability and personal calendars.
// Two organisations, so clashes across organisations are real. Rules here: Monday to Friday, 09:00–18:00, starts
// every 15 minutes, 15 minutes' changeover per room, at most 4 teaching hours a day.
public sealed class ScheduleServiceTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SecretariArtDbContext _db;
    private readonly MovableClock _clock = new();
    private readonly RulesService _rules;
    private readonly ClassroomService _classrooms;
    private readonly CourseService _courses;
    private readonly EnrolmentService _enrolments;
    private readonly ScheduleService _schedule;
    private readonly AvailabilityService _availability;

    private readonly int _org;
    private readonly int _otherOrg;
    private readonly ApplicationUser _teacher;
    private readonly ApplicationUser _student;
    private readonly int _hall;
    private readonly int _pianoRoom;
    private readonly int _actingA;
    private readonly int _voiceA;

    public ScheduleServiceTests()
    {
        _connection.Open();
        _db = new SecretariArtDbContext(new DbContextOptionsBuilder<SecretariArtDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();

        var memberships = new MembershipService(_db, _clock);
        var notifications = new NotificationService(_db, _clock);
        _rules = new RulesService(_db, _clock);
        _classrooms = new ClassroomService(_db);
        _courses = new CourseService(_db, _rules, memberships, notifications, _clock);
        _enrolments = new EnrolmentService(_db, memberships, _clock, notifications);
        _schedule = new ScheduleService(_db, _rules, notifications, _clock);
        _availability = new AvailabilityService(_db, memberships, _rules);

        ApplicationUser owner = AddUser("owner");
        _org = AddOrganization("Athens", owner);
        _otherOrg = AddOrganization("Patras", owner);
        SaveRules(_org);

        _teacher = AddMember("teacher", _org, MembershipRole.Teacher);
        _student = AddMember("student", _org, MembershipRole.Student);

        _hall = AddRoom(_org, "Hall", 30);
        _pianoRoom = AddRoom(_org, "Piano room", 10, RoomFeatures.Piano);

        _actingA = AddGroup(_org, "Acting", 120, 2, RoomFeatures.None, "A", _teacher);
        _voiceA = AddGroup(_org, "Voice", 60, 1, RoomFeatures.Piano, "A", _teacher);
        Enrol(_org, _student, _actingA);
        Enrol(_org, _student, _voiceA);
    }

    // ---- Setup helpers ----

    private ApplicationUser AddUser(string name)
    {
        var user = new ApplicationUser { UserName = $"{name}@test.example", Email = $"{name}@test.example", FirstName = name, LastName = "Test" };
        _db.Users.Add(user);
        _db.SaveChanges();
        return user;
    }

    private int AddOrganization(string name, ApplicationUser owner)
    {
        var organization = new Organization
        {
            Name = name, City = name, ContactEmail = "x@test.example", Status = OrganizationStatus.Active,
            InviteCode = name.ToUpperInvariant(), RequestedById = owner.Id,
        };
        _db.Organizations.Add(organization);
        _db.SaveChanges();
        return organization.Id;
    }

    private void SaveRules(int organizationId, DateOnly? termStarts = null, DateOnly? termEnds = null) =>
        Assert.True(_rules.SaveAsync(organizationId, new RulesInput(
            TeachingDays.Monday | TeachingDays.Tuesday | TeachingDays.Wednesday | TeachingDays.Thursday | TeachingDays.Friday,
            new TimeOnly(9, 0), new TimeOnly(18, 0), 15, 45, 180, 1, 3, 15, 4, termStarts, termEnds)).Result.Succeeded);

    private ApplicationUser AddMember(string name, int organizationId, MembershipRole role)
    {
        ApplicationUser user = AddUser(name);
        AddRole(user, organizationId, role);
        return user;
    }

    private void AddRole(ApplicationUser user, int organizationId, MembershipRole role)
    {
        _db.Memberships.Add(new Membership { UserId = user.Id, OrganizationId = organizationId, Role = role, Status = MembershipStatus.Active });
        _db.SaveChanges();
    }

    private int AddRoom(int organizationId, string name, int capacity, RoomFeatures features = RoomFeatures.None)
    {
        var room = new Classroom { OrganizationId = organizationId, Name = name, Capacity = capacity, Features = features };
        _db.Classrooms.Add(room);
        _db.SaveChanges();
        return room.Id;
    }

    private int AddGroup(int organizationId, string course, int minutes, int perWeek, RoomFeatures needs, string group, ApplicationUser? teacher, int capacity = 10)
    {
        Course? existing = _db.Courses.FirstOrDefault(c => c.OrganizationId == organizationId && c.Name == course);
        if (existing == null)
        {
            existing = new Course { OrganizationId = organizationId, Name = course, SessionMinutes = minutes, SessionsPerWeek = perWeek, RequiredFeatures = needs };
            if (teacher != null)
            {
                existing.Teachers.Add(new CourseTeacher { TeacherId = teacher.Id });
            }
            _db.Courses.Add(existing);
        }
        var g = new CourseGroup { OrganizationId = organizationId, Course = existing, Name = group, Capacity = capacity, TeacherId = teacher?.Id };
        _db.CourseGroups.Add(g);
        _db.SaveChanges();
        return g.Id;
    }

    private void Enrol(int organizationId, ApplicationUser student, int groupId)
    {
        CourseGroup group = _db.CourseGroups.Single(g => g.Id == groupId);
        _db.Enrolments.Add(new Enrolment { OrganizationId = organizationId, StudentId = student.Id, CourseId = group.CourseId, GroupId = groupId });
        _db.SaveChanges();
    }

    // Puts a session straight in the database (for setting up another organisation's timetable).
    private void Place(int organizationId, int groupId, int roomId, DayOfWeek day, int hour, int minutes)
    {
        var starts = new TimeOnly(hour, 0);
        _db.ClassSessions.Add(new ClassSession { OrganizationId = organizationId, GroupId = groupId, ClassroomId = roomId, Day = day, Starts = starts, Ends = starts.AddMinutes(minutes) });
        _db.SaveChanges();
    }

    private Task<ServiceResult> AddAsync(int groupId, int roomId, DayOfWeek day, int hour, int minute = 0) =>
        _schedule.CreateAsync(_org, new SessionInput(groupId, roomId, day, new TimeOnly(hour, minute)));

    private List<string> NotificationsOf(ApplicationUser user) =>
        _db.Notifications.AsNoTracking().Where(n => n.UserId == user.Id).Select(n => n.Text).ToList();

    // ---- Creating, moving, removing ----

    [Fact]
    public async Task AClassInsideTheRules_IsSaved_AndItsTeacherAndStudentsAreTold()
    {
        ServiceResult result = await AddAsync(_actingA, _hall, DayOfWeek.Monday, 10);

        Assert.True(result.Succeeded, result.Error);
        ClassSession session = _db.ClassSessions.AsNoTracking().Single();
        Assert.Equal(new TimeOnly(12, 0), session.Ends); // the course's 2 hours
        Assert.Contains("Athens: New class: Acting (group A) on Monday 10:00–12:00 in Hall.", NotificationsOf(_student));
        Assert.Single(NotificationsOf(_teacher));
    }

    [Fact]
    public async Task MovingAndRemovingAClass_TellTheSamePeople()
    {
        await AddAsync(_actingA, _hall, DayOfWeek.Monday, 10);
        int id = _db.ClassSessions.Single().Id;

        ServiceResult moved = await _schedule.UpdateAsync(_org, id, new SessionInput(_actingA, _hall, DayOfWeek.Tuesday, new TimeOnly(14, 0)));
        ServiceResult removed = await _schedule.DeleteAsync(_org, id);

        Assert.StartsWith("Acting (group A) moved to Tuesday 14:00–16:00 in Hall.", moved.Message);
        Assert.True(removed.Succeeded);
        Assert.Contains("Athens: Acting (group A) moved from Monday 10:00–12:00 in Hall to Tuesday 14:00–16:00 in Hall.", NotificationsOf(_student));
        Assert.Contains("Athens: Acting (group A) on Tuesday 14:00–16:00 has been taken off the timetable.", NotificationsOf(_teacher));
        Assert.Empty(_db.ClassSessions);
    }

    [Fact]
    public async Task AnotherOrganisationsClass_CannotBeMovedOrRemoved()
    {
        ApplicationUser theirTeacher = AddMember("their-teacher", _otherOrg, MembershipRole.Teacher);
        int theirGroup = AddGroup(_otherOrg, "Ballet", 60, 1, RoomFeatures.None, "A", theirTeacher);
        int theirRoom = AddRoom(_otherOrg, "Studio", 10);
        Place(_otherOrg, theirGroup, theirRoom, DayOfWeek.Monday, 10, 60);
        int theirs = _db.ClassSessions.Single().Id;

        Assert.False((await _schedule.UpdateAsync(_org, theirs, new SessionInput(theirGroup, _hall, DayOfWeek.Friday, new TimeOnly(9, 0)))).Succeeded);
        Assert.False((await _schedule.DeleteAsync(_org, theirs)).Succeeded);
        Assert.False((await _schedule.CreateAsync(_org, new SessionInput(theirGroup, _hall, DayOfWeek.Friday, new TimeOnly(9, 0)))).Succeeded);
        Assert.Null(await _schedule.FindAsync(_org, theirs));
        Assert.Single(_db.ClassSessions);
    }

    // ---- Problems that stop a class being saved ----

    [Fact]
    public async Task ClassesOutsideTheRules_AreRefused()
    {
        ServiceResult saturday = await AddAsync(_actingA, _hall, DayOfWeek.Saturday, 10);
        ServiceResult late = await AddAsync(_actingA, _hall, DayOfWeek.Monday, 17);
        ServiceResult offGrid = await AddAsync(_actingA, _hall, DayOfWeek.Monday, 10, 10);

        Assert.Contains("Athens doesn't teach on Saturdays", saturday.Error);
        Assert.Contains("Classes must fit between 09:00 and 18:00 (see Rules); this one would end at 19:00.", late.Error);
        Assert.Contains("Classes start every 15 minutes from 09:00", offGrid.Error);
        Assert.Empty(_db.ClassSessions);
    }

    [Fact]
    public async Task ARoomWithoutWhatTheCourseNeeds_OrTooSmallForItsStudents_IsRefused()
    {
        int tiny = AddRoom(_org, "Booth", 1);
        Enrol(_org, AddMember("second", _org, MembershipRole.Student), _actingA);

        ServiceResult noPiano = await AddAsync(_voiceA, _hall, DayOfWeek.Monday, 10);
        ServiceResult tooSmall = await AddAsync(_actingA, tiny, DayOfWeek.Monday, 10);

        Assert.Contains("Hall doesn't have piano, which Voice needs.", noPiano.Error);
        Assert.Contains("Booth holds 1, but group A has 2 students.", tooSmall.Error);
    }

    [Fact]
    public async Task ARoomIsBooked_IncludingTheChangeoverTime()
    {
        ApplicationUser other = AddMember("other-teacher", _org, MembershipRole.Teacher);
        int drawing = AddGroup(_org, "Drawing", 60, 1, RoomFeatures.None, "A", other);
        await AddAsync(_actingA, _hall, DayOfWeek.Monday, 10); // 10:00–12:00

        ServiceResult overlapping = await AddAsync(drawing, _hall, DayOfWeek.Monday, 11);
        ServiceResult noChangeover = await AddAsync(drawing, _hall, DayOfWeek.Monday, 12);
        ServiceResult afterChangeover = await AddAsync(drawing, _hall, DayOfWeek.Monday, 12, 15);

        Assert.Contains("Hall is taken by Acting (group A) on Monday 10:00–12:00.", overlapping.Error);
        Assert.Contains("and the room needs 15 minutes between classes", noChangeover.Error);
        Assert.True(afterChangeover.Succeeded, afterChangeover.Error);
    }

    [Fact]
    public async Task ATeacher_CannotTeachTwoClassesAtOnce_HereOrElsewhere()
    {
        await AddAsync(_actingA, _hall, DayOfWeek.Monday, 10);
        ServiceResult here = await AddAsync(_voiceA, _pianoRoom, DayOfWeek.Monday, 11);

        AddRole(_teacher, _otherOrg, MembershipRole.Teacher);
        int ballet = AddGroup(_otherOrg, "Ballet", 60, 1, RoomFeatures.None, "A", _teacher);
        Place(_otherOrg, ballet, AddRoom(_otherOrg, "Studio", 10), DayOfWeek.Wednesday, 10, 60);
        ServiceResult elsewhere = await AddAsync(_voiceA, _pianoRoom, DayOfWeek.Wednesday, 10);

        Assert.Contains("teacher Test teaches Acting (group A) on Monday 10:00–12:00.", here.Error);
        Assert.Contains("teacher Test teaches at another organisation on Wednesday 10:00–11:00.", elsewhere.Error);
    }

    [Fact]
    public async Task TeachingElsewhere_OnlyClashesWhenTheTermsOverlap()
    {
        SaveRules(_org, new DateOnly(2026, 9, 1), new DateOnly(2026, 12, 20));
        SaveRules(_otherOrg, new DateOnly(2027, 2, 1), new DateOnly(2027, 6, 15)); // spring only
        AddRole(_teacher, _otherOrg, MembershipRole.Teacher);
        int ballet = AddGroup(_otherOrg, "Ballet", 60, 1, RoomFeatures.None, "A", _teacher);
        Place(_otherOrg, ballet, AddRoom(_otherOrg, "Studio", 10), DayOfWeek.Wednesday, 10, 60);

        ServiceResult result = await AddAsync(_voiceA, _pianoRoom, DayOfWeek.Wednesday, 10);

        Assert.True(result.Succeeded, result.Error);
    }

    [Fact]
    public async Task AStudent_CannotHaveTwoClassesAtOnceHere_ButAClassElsewhereIsOnlyAWarning()
    {
        ApplicationUser other = AddMember("other-teacher", _org, MembershipRole.Teacher);
        int drawing = AddGroup(_org, "Drawing", 60, 1, RoomFeatures.None, "A", other);
        Enrol(_org, _student, drawing);
        await AddAsync(_actingA, _hall, DayOfWeek.Monday, 10);

        ServiceResult clash = await AddAsync(drawing, _pianoRoom, DayOfWeek.Monday, 11);
        Assert.Contains("student Test also takes Acting (group A) on Monday 10:00–12:00.", clash.Error);

        AddRole(_student, _otherOrg, MembershipRole.Student);
        int ballet = AddGroup(_otherOrg, "Ballet", 60, 1, RoomFeatures.None, "A", null);
        Enrol(_otherOrg, _student, ballet);
        Place(_otherOrg, ballet, AddRoom(_otherOrg, "Studio", 10), DayOfWeek.Tuesday, 10, 60);
        ServiceResult elsewhere = await AddAsync(drawing, _pianoRoom, DayOfWeek.Tuesday, 10);

        Assert.True(elsewhere.Succeeded, elsewhere.Error);
        Assert.Contains("1 student has a class at another organisation then.", elsewhere.Message);
    }

    [Fact]
    public async Task AStudentWhoTeachesElsewhere_IsFlaggedHereToo()
    {
        // QA, S1: a student here who teaches at another organisation at the same time.
        ApplicationUser other = AddMember("other-teacher", _org, MembershipRole.Teacher);
        int drawing = AddGroup(_org, "Drawing", 60, 1, RoomFeatures.None, "A", other);
        Enrol(_org, _student, drawing);
        AddRole(_student, _otherOrg, MembershipRole.Teacher);
        int piano = AddGroup(_otherOrg, "Piano", 45, 1, RoomFeatures.None, "A", _student);
        Place(_otherOrg, piano, AddRoom(_otherOrg, "Studio", 10), DayOfWeek.Tuesday, 16, 45);

        ServiceResult result = await AddAsync(drawing, _pianoRoom, DayOfWeek.Tuesday, 16);

        Assert.True(result.Succeeded, result.Error);
        Assert.Contains("1 student has a class at another organisation then.", result.Message);
    }

    // ---- Warnings: saved, but said ----

    [Fact]
    public async Task Availability_IsCheckedOnlyAsAWarning()
    {
        ServiceResult notPosted = await AddAsync(_actingA, _hall, DayOfWeek.Monday, 10);
        Assert.Contains("teacher Test hasn't posted their availability yet.", notPosted.Message);

        await _availability.AddAsync(_teacher.Id, new AvailabilityInput(_org, DayOfWeek.Tuesday, new TimeOnly(9, 0), new TimeOnly(13, 0)));
        ServiceResult inside = await AddAsync(_actingA, _hall, DayOfWeek.Tuesday, 10);
        ServiceResult outside = await AddAsync(_voiceA, _pianoRoom, DayOfWeek.Wednesday, 10);

        Assert.DoesNotContain("availability", inside.Message);
        Assert.Contains("It's outside teacher Test's availability.", outside.Message);
    }

    [Fact]
    public async Task TooManyTeachingHoursInADay_IsAWarning()
    {
        await AddAsync(_actingA, _hall, DayOfWeek.Monday, 9);  // 2 h
        await AddAsync(_actingA, _hall, DayOfWeek.Monday, 12); // 4 h

        ServiceResult fifth = await AddAsync(_voiceA, _pianoRoom, DayOfWeek.Monday, 15); // 5 h

        Assert.True(fifth.Succeeded);
        Assert.Contains("would teach 5 h on Monday, more than the 4 h limit", fifth.Message);
    }

    [Fact]
    public async Task ExistingClasses_AreFlaggedWhenSomethingChangesAround_Them()
    {
        await AddAsync(_actingA, _hall, DayOfWeek.Monday, 10);
        int acting = _db.Courses.Single(c => c.Name == "Acting").Id;
        ServiceResult shorter = await _courses.UpdateAsync(_org, acting, new CourseInput("Acting", null, 90, 2, RoomFeatures.None, [_teacher.Id]));

        SessionView view = (await _schedule.GetAsync(_org)).Sessions.Single();

        Assert.Contains("It lasts 2 h, but Acting sessions last 1 h 30 min.", view.Warnings);
        Assert.Contains("1 class in the timetable doesn't last 1 h 30 min yet: it's flagged", shorter.Message);

        // Putting the length back matches the class again: nothing to flag (QA, S3).
        ServiceResult back = await _courses.UpdateAsync(_org, acting, new CourseInput("Acting", null, 120, 2, RoomFeatures.None, [_teacher.Id]));
        Assert.Equal("Acting saved.", back.Message);
    }

    [Fact]
    public void WeekGrid_SplitsOnlyOverlappingClasses()
    {
        // QA, S7: two overlapping evening classes mustn't halve a morning class the same day.
        var blocks = new List<WeekBlock>
        {
            new(DayOfWeek.Monday, new TimeOnly(10, 0), new TimeOnly(10, 45), "Piano", null, null, null),
            new(DayOfWeek.Monday, new TimeOnly(18, 0), new TimeOnly(19, 0), "Acting", null, null, null),
            new(DayOfWeek.Monday, new TimeOnly(18, 30), new TimeOnly(19, 30), "Voice", null, null, null),
        };
        var grid = WeekGridModel.Fit(blocks, [DayOfWeek.Monday], new TimeOnly(9, 0), new TimeOnly(20, 0), "Week");

        var lanes = grid.Lanes().ToDictionary(p => p.Block.Title, p => p.Lanes);

        Assert.Equal(1, lanes["Piano"]);
        Assert.Equal(2, lanes["Acting"]);
        Assert.Equal(2, lanes["Voice"]);
    }

    // ---- Suggestions ----

    [Fact]
    public async Task Suggestions_OnlyOfferFreeSlots_AndPreferTheTeachersAvailability()
    {
        await _availability.AddAsync(_teacher.Id, new AvailabilityInput(_org, DayOfWeek.Thursday, new TimeOnly(14, 0), new TimeOnly(18, 0)));
        await AddAsync(_actingA, _hall, DayOfWeek.Thursday, 14); // the teacher's Thursday 14–16

        var suggestions = await _schedule.SuggestAsync(_org, _voiceA);

        Assert.NotEmpty(suggestions);
        Assert.All(suggestions, s => Assert.Equal("Piano room", s.Room.Name));
        // Thursday's best is inside the availability, after the Acting class.
        Suggestion best = suggestions.First(s => s.Day == DayOfWeek.Thursday);
        Assert.True(best.Starts >= new TimeOnly(16, 0), $"{best.Starts} overlaps the Acting class");
        Assert.Empty(best.Warnings);
    }

    [Fact]
    public async Task Suggestions_CoverTheWholeWeek_AndPutTheGroupsOwnDaysLast()
    {
        await AddAsync(_voiceA, _pianoRoom, DayOfWeek.Monday, 10);

        var suggestions = await _schedule.SuggestAsync(_org, _voiceA);

        var days = suggestions.Select(s => s.Day).Distinct().ToList();
        Assert.True(days.Count > 2, $"only {string.Join(", ", days)}");
        Assert.All(suggestions.GroupBy(s => s.Day), d => Assert.True(d.Count() <= 3));
        Assert.Equal(DayOfWeek.Monday, days[^1]); // it already meets on Monday
    }

    // ---- Changes elsewhere that would create a clash ----

    [Fact]
    public async Task EnrollingInAGroupThatClashesWithTheStudentsClasses_IsRefused()
    {
        ApplicationUser other = AddMember("other-teacher", _org, MembershipRole.Teacher);
        int drawing = AddGroup(_org, "Drawing", 60, 1, RoomFeatures.None, "A", other);
        await AddAsync(_actingA, _hall, DayOfWeek.Monday, 10);
        await AddAsync(drawing, _pianoRoom, DayOfWeek.Monday, 11);

        ServiceResult result = await _enrolments.EnrolAsync(_org, _student.Id, drawing);

        Assert.Contains("meets at the same time as student Test's Acting (group A) on Monday 10:00–12:00", result.Error);
    }

    [Fact]
    public async Task AGroupsNewTeacher_MustBeFreeAtItsClassTimes()
    {
        ApplicationUser other = AddMember("other-teacher", _org, MembershipRole.Teacher);
        int drawing = AddGroup(_org, "Drawing", 60, 1, RoomFeatures.None, "A", other);
        Course drawingCourse = _db.Courses.Single(c => c.Name == "Drawing");
        _db.CourseTeachers.Add(new CourseTeacher { CourseId = drawingCourse.Id, TeacherId = _teacher.Id });
        _db.SaveChanges();
        await AddAsync(_actingA, _hall, DayOfWeek.Monday, 10);
        await AddAsync(drawing, _pianoRoom, DayOfWeek.Monday, 11);

        ServiceResult result = await _courses.UpdateGroupAsync(_org, drawing, new GroupInput("A", 10, _teacher.Id));

        Assert.Contains("That teacher already teaches Acting (group A) on Monday 10:00–12:00", result.Error);
    }

    [Fact]
    public async Task ARoomInTheTimetable_CannotBeDeleted_AndDeletingAGroupTakesItsClassesOff()
    {
        await AddAsync(_actingA, _hall, DayOfWeek.Monday, 10);

        ServiceResult room = await _classrooms.DeleteAsync(_org, _hall);
        ServiceResult group = await _courses.DeleteGroupAsync(_org, _actingA);

        Assert.Contains("Hall is used by 1 class in the timetable.", room.Error);
        Assert.Contains("1 class came off the timetable", group.Message);
        Assert.Empty(_db.ClassSessions);
        Assert.Contains("Athens: group A of Acting has been closed; its classes are off the timetable.", NotificationsOf(_student));
    }

    // ---- Personal calendar and availability ----

    [Fact]
    public async Task APersonalCalendar_ShowsClassesFromEveryOrganisation_AndMarksClashes()
    {
        AddRole(_student, _otherOrg, MembershipRole.Student);
        int ballet = AddGroup(_otherOrg, "Ballet", 60, 1, RoomFeatures.None, "A", null);
        Enrol(_otherOrg, _student, ballet);
        Place(_otherOrg, ballet, AddRoom(_otherOrg, "Studio", 10), DayOfWeek.Monday, 11, 60);
        await AddAsync(_actingA, _hall, DayOfWeek.Monday, 10);

        var week = await _schedule.GetPersonalAsync(_student.Id);

        Assert.Equal(2, week.Count);
        Assert.All(week, p => Assert.NotNull(p.Clash));
        Assert.Equal("Clashes with Ballet at Patras (11:00–12:00).", week.Single(p => p.Organization.Name == "Athens").Clash);
        PersonalSession taught = Assert.Single(await _schedule.GetPersonalAsync(_teacher.Id));
        Assert.True(taught.Teaching);
        Assert.Null(taught.Clash);
    }

    [Fact]
    public async Task Availability_MergesTouchingTimes_AndIsOnlyForTeachersThere()
    {
        await _availability.AddAsync(_teacher.Id, new AvailabilityInput(_org, DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(12, 0)));
        ServiceResult merged = await _availability.AddAsync(_teacher.Id, new AvailabilityInput(_org, DayOfWeek.Monday, new TimeOnly(12, 0), new TimeOnly(15, 0)));
        ServiceResult notThere = await _availability.AddAsync(_teacher.Id, new AvailabilityInput(_otherOrg, DayOfWeek.Monday, new TimeOnly(9, 0), new TimeOnly(12, 0)));
        ServiceResult backwards = await _availability.AddAsync(_teacher.Id, new AvailabilityInput(_org, DayOfWeek.Friday, new TimeOnly(12, 0), new TimeOnly(9, 0)));

        Assert.Equal("Saved: Monday 09:00–15:00 (joined with what you'd already posted).", merged.Message);
        Assert.False(notThere.Succeeded);
        Assert.False(backwards.Succeeded);
        Assert.Single(_db.TeacherAvailability);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
