using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using SecretariArt.Data;
using SecretariArt.Models;
using SecretariArt.Services;

namespace SecretariArt.Tests.Services;

// Milestone 2: rules, classrooms, courses, groups, bundles and enrolments, against a fresh in-memory database with two
// organisations (so every "other organisation" case is real).
public sealed class AdministrationServiceTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SecretariArtDbContext _db;
    private readonly MovableClock _clock = new();
    private readonly RulesService _rules;
    private readonly ClassroomService _classrooms;
    private readonly CourseService _courses;
    private readonly EnrolmentService _enrolments;

    private readonly int _org;
    private readonly int _otherOrg;
    private readonly ApplicationUser _teacher;
    private readonly ApplicationUser _otherTeacher;

    public AdministrationServiceTests()
    {
        _connection.Open();
        _db = new SecretariArtDbContext(new DbContextOptionsBuilder<SecretariArtDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();

        var memberships = new MembershipService(_db, _clock);
        _rules = new RulesService(_db, _clock);
        _classrooms = new ClassroomService(_db);
        _courses = new CourseService(_db, _rules, memberships, new NotificationService(_db, _clock), _clock);
        _enrolments = new EnrolmentService(_db, memberships, _clock, new NotificationService(_db, _clock));

        ApplicationUser owner = AddUser("owner");
        _org = AddOrganization("Athens", owner).Id;
        _otherOrg = AddOrganization("Patras", owner).Id;
        _teacher = AddMember("teacher", _org, MembershipRole.Teacher);
        _otherTeacher = AddMember("elsewhere", _otherOrg, MembershipRole.Teacher);
    }

    // ---- Setup helpers ----

    private ApplicationUser AddUser(string name)
    {
        var user = new ApplicationUser { UserName = $"{name}@test.example", Email = $"{name}@test.example", FirstName = name, LastName = "Test" };
        _db.Users.Add(user);
        _db.SaveChanges();
        return user;
    }

    private Organization AddOrganization(string name, ApplicationUser owner)
    {
        var organization = new Organization
        {
            Name = name, City = name, ContactEmail = "x@test.example", Status = OrganizationStatus.Active,
            InviteCode = name.ToUpperInvariant(), RequestedById = owner.Id,
        };
        _db.Organizations.Add(organization);
        _db.SaveChanges();
        return organization;
    }

    private ApplicationUser AddMember(string name, int organizationId, MembershipRole role, MembershipStatus status = MembershipStatus.Active)
    {
        ApplicationUser user = AddUser(name);
        _db.Memberships.Add(new Membership { UserId = user.Id, OrganizationId = organizationId, Role = role, Status = status });
        _db.SaveChanges();
        return user;
    }

    private static CourseInput Course(string name, int minutes = 90, int perWeek = 1, RoomFeatures needs = RoomFeatures.None, params string[] teachers) =>
        new(name, null, minutes, perWeek, needs, teachers);

    private async Task<int> AddCourseAsync(string name, params string[] teachers)
    {
        ServiceResult result = await _courses.CreateAsync(_org, Course(name, teachers: teachers));
        Assert.True(result.Succeeded, result.Error);
        return result.CreatedId!.Value;
    }

    private async Task<int> AddGroupAsync(int courseId, string name, int capacity = 10)
    {
        Assert.True((await _courses.CreateGroupAsync(_org, courseId, new GroupInput(name, capacity, null))).Succeeded);
        return _db.CourseGroups.Single(g => g.CourseId == courseId && g.Name == name).Id;
    }

    private static ClassroomInput Room(string name, int capacity = 20, RoomFeatures features = RoomFeatures.None) =>
        new(name, capacity, FloorType.Standard, features, false, null);

    // ---- Rules ----

    [Fact]
    public async Task Rules_StartAsUnsavedDefaults()
    {
        OrganizationRules rules = await _rules.GetAsync(_org);

        Assert.False(await _rules.HasSavedAsync(_org));
        Assert.Equal(OrganizationRulesDefaults.MinSessionMinutes, rules.MinSessionMinutes);
        Assert.Equal(new TimeOnly(9, 0), rules.DayStarts);
    }

    [Fact]
    public async Task Rules_WithTheDayEndingBeforeItStarts_AreRefused()
    {
        var input = new RulesInput(TeachingDays.Monday, new TimeOnly(18, 0), new TimeOnly(9, 0), 15, 45, 180, 1, 5, 0, null, null, null);

        ServiceResult result = await _rules.SaveAsync(_org, input);

        Assert.False(result.Succeeded);
        Assert.Equal("The day must end after it starts.", result.Error);
        Assert.False(await _rules.HasSavedAsync(_org));
    }

    [Fact]
    public async Task TighterRules_AreSaved_ButNameTheCoursesThatNoLongerFit()
    {
        await _courses.CreateAsync(_org, Course("Acting I", minutes: 180));

        ServiceResult result = await _rules.SaveAsync(_org,
            new RulesInput(TeachingDays.Monday | TeachingDays.Friday, new TimeOnly(9, 0), new TimeOnly(17, 0), 15, 45, 120, 1, 3, 10, null, null, null));

        Assert.True(result.Succeeded);
        Assert.Contains("1 course no longer fits them: Acting I", result.Message);
        Assert.Contains((await _courses.GetAllAsync(_org)).Single().Warnings, w => w.StartsWith("Sessions must last between"));
    }

    // ---- Classrooms ----

    [Fact]
    public async Task ClassroomNames_AreUniquePerOrganisation_IgnoringCase()
    {
        await _classrooms.CreateAsync(_org, Room("Studio A"));

        ServiceResult duplicate = await _classrooms.CreateAsync(_org, Room("studio a "));
        ServiceResult elsewhere = await _classrooms.CreateAsync(_otherOrg, Room("Studio A"));

        Assert.False(duplicate.Succeeded);
        Assert.Equal("There's already a classroom called studio a.", duplicate.Error);
        Assert.True(elsewhere.Succeeded);
    }

    [Fact]
    public async Task AnotherOrganisationsClassroom_CannotBeSeenChangedOrDeleted()
    {
        await _classrooms.CreateAsync(_otherOrg, Room("Their studio"));
        int theirs = _db.Classrooms.Single().Id;

        Assert.Null(await _classrooms.FindAsync(_org, theirs));
        Assert.False((await _classrooms.UpdateAsync(_org, theirs, Room("Mine now"))).Succeeded);
        Assert.False((await _classrooms.DeleteAsync(_org, theirs)).Succeeded);
        Assert.Equal("Their studio", _db.Classrooms.AsNoTracking().Single().Name);
    }

    // ---- Courses and groups ----

    [Fact]
    public async Task ACourseOutsideTheRules_IsRefused()
    {
        ServiceResult tooLong = await _courses.CreateAsync(_org, Course("Marathon", minutes: 600));
        ServiceResult offGrid = await _courses.CreateAsync(_org, Course("Odd", minutes: 50));
        ServiceResult tooOften = await _courses.CreateAsync(_org, Course("Daily", perWeek: 7));

        Assert.StartsWith("Sessions must last between 45 min and 3 h", tooLong.Error);
        Assert.Equal("Session length must be a multiple of 15 minutes (see Rules).", offGrid.Error);
        Assert.Equal("Sessions per week must be between 1 and 5 (see Rules).", tooOften.Error);
        Assert.Empty(_db.Courses);
    }

    [Fact]
    public async Task OnlyThisOrganisationsApprovedTeachers_CanTeachACourse()
    {
        ApplicationUser pending = AddMember("pending", _org, MembershipRole.Teacher, MembershipStatus.Pending);

        ServiceResult otherOrg = await _courses.CreateAsync(_org, Course("Voice", teachers: _otherTeacher.Id));
        ServiceResult notApproved = await _courses.CreateAsync(_org, Course("Voice", teachers: pending.Id));
        ServiceResult ok = await _courses.CreateAsync(_org, Course("Voice", teachers: _teacher.Id));

        Assert.False(otherOrg.Succeeded);
        Assert.False(notApproved.Succeeded);
        Assert.True(ok.Succeeded);
    }

    [Fact]
    public async Task AGroupsTeacher_MustTeachTheCourse_AndLosesTheGroupWhenTakenOffIt()
    {
        int courseId = await AddCourseAsync("Acting I", _teacher.Id);
        ApplicationUser second = AddMember("second", _org, MembershipRole.Teacher);

        ServiceResult notOnCourse = await _courses.CreateGroupAsync(_org, courseId, new GroupInput("A", 10, second.Id));
        Assert.False(notOnCourse.Succeeded);
        Assert.True((await _courses.CreateGroupAsync(_org, courseId, new GroupInput("A", 10, _teacher.Id))).Succeeded);

        ServiceResult saved = await _courses.UpdateAsync(_org, courseId, Course("Acting I", teachers: second.Id));

        Assert.Contains("1 group has no teacher now", saved.Message);
        Assert.Null(_db.CourseGroups.AsNoTracking().Single().TeacherId);
    }

    [Fact]
    public async Task Names_IgnoreGreekAccentsAndCapitals()
    {
        // Greek capitals drop the accent: "ΧΑΡΑΚΤΙΚΗ" is the same room as "Χαρακτική" (QA, M1).
        await _classrooms.CreateAsync(_org, Room("Χαρακτική"));
        Assert.Equal("There's already a classroom called ΧΑΡΑΚΤΙΚΗ.", (await _classrooms.CreateAsync(_org, Room("ΧΑΡΑΚΤΙΚΗ"))).Error);
        Assert.False((await _classrooms.CreateAsync(_org, Room("χαρακτικη"))).Succeeded);

        await AddCourseAsync("Θέατρο Δρόμου");
        Assert.False((await _courses.CreateAsync(_org, Course("ΘΕΑΤΡΟ ΔΡΟΜΟΥ"))).Succeeded);

        Assert.True(TextMatch.Contains("Άννα Γεωργίου", "ΓΕΩΡΓΙΟΥ"));
        Assert.True(TextMatch.Contains("Άννα Γεωργίου", "αννα"));
        Assert.True(TextMatch.SameName("Τέχνες", "ΤΕΧΝΕΣ"));
    }

    [Fact]
    public async Task ATeacher_CannotBeAStudentInTheGroupTheyTeach()
    {
        // Someone who both teaches and studies here (QA, M6).
        ApplicationUser both = AddMember("both", _org, MembershipRole.Teacher);
        _db.Memberships.Add(new Membership { UserId = both.Id, OrganizationId = _org, Role = MembershipRole.Student, Status = MembershipStatus.Active });
        _db.SaveChanges();
        int courseId = await AddCourseAsync("Ensemble", both.Id);
        Assert.True((await _courses.CreateGroupAsync(_org, courseId, new GroupInput("A", 10, both.Id))).Succeeded);
        int taught = _db.CourseGroups.Single(g => g.Name == "A").Id;
        int other = await AddGroupAsync(courseId, "B");

        ServiceResult one = await _enrolments.EnrolAsync(_org, both.Id, taught);
        Assert.Equal("both Test teaches group A of Ensemble, so can't also be a student in it. Choose another group.", one.Error);
        Assert.False((await _enrolments.EnrolManyAsync(_org, taught, [both.Id])).Succeeded);

        // With only their own group, "choose another group" would be no help.
        int solo = await AddCourseAsync("Solo", both.Id);
        Assert.True((await _courses.CreateGroupAsync(_org, solo, new GroupInput("A", 5, both.Id))).Succeeded);
        int soloGroup = _db.CourseGroups.Single(g => g.CourseId == solo).Id;
        Assert.EndsWith("Add another group to the course, or give this one a different teacher.",
            (await _enrolments.EnrolAsync(_org, both.Id, soloGroup)).Error);

        // Through a bundle they're placed in another group, never their own.
        await _courses.CreateBundleAsync(_org, new BundleInput("Year 1", null, [courseId]));
        Assert.True((await _enrolments.EnrolInBundleAsync(_org, both.Id, _db.Bundles.Single().Id, "A")).Succeeded);
        Assert.Equal(other, _db.Enrolments.AsNoTracking().Single(e => e.StudentId == both.Id).GroupId);

        // And the other way round: a group's student can't be made its teacher.
        ServiceResult swap = await _courses.UpdateGroupAsync(_org, other, new GroupInput("B", 10, both.Id));
        Assert.False(swap.Succeeded);
        Assert.Contains("enrolled as a student in this group", swap.Error);
    }

    [Fact]
    public async Task GroupNames_AreUniqueWithinACourse()
    {
        int courseId = await AddCourseAsync("Acting I");
        await AddGroupAsync(courseId, "A");

        ServiceResult duplicate = await _courses.CreateGroupAsync(_org, courseId, new GroupInput("a", 10, null));

        Assert.Equal("Acting I already has a group called a.", duplicate.Error);
    }

    [Fact]
    public async Task Warnings_NameMissingRoomsTeachersAndGroups()
    {
        await _classrooms.CreateAsync(_org, Room("Small studio", capacity: 8, features: RoomFeatures.Mirrors));
        await _courses.CreateAsync(_org, Course("Piano", needs: RoomFeatures.Piano));
        await _courses.CreateAsync(_org, Course("Ballet", needs: RoomFeatures.Mirrors, teachers: _teacher.Id));
        int ballet = _db.Courses.Single(c => c.Name == "Ballet").Id;
        int bigGroup = await AddGroupAsync(ballet, "A", capacity: 12);

        var courses = await _courses.GetAllAsync(_org);
        CourseOverview piano = courses.Single(c => c.Course.Name == "Piano");
        CourseOverview balletOverview = courses.Single(c => c.Course.Name == "Ballet");

        Assert.Contains("No classroom has everything it needs (Piano).", piano.Warnings);
        Assert.Contains("No teacher is assigned to it yet.", piano.Warnings);
        Assert.Contains("It has no groups yet, so no one can be enrolled.", piano.Warnings);
        Assert.Empty(balletOverview.Warnings);
        Assert.Equal("12 places, but the largest suitable room holds 8.", balletOverview.GroupWarnings[bigGroup]);
    }

    [Fact]
    public async Task DeletingACourse_RemovesItsGroupsAndEnrolments()
    {
        ApplicationUser student = AddMember("student", _org, MembershipRole.Student);
        int courseId = await AddCourseAsync("Acting I");
        int group = await AddGroupAsync(courseId, "A");
        await _enrolments.EnrolAsync(_org, student.Id, group);

        ServiceResult result = await _courses.DeleteAsync(_org, courseId);

        Assert.Equal("Acting I deleted, with its 1 group and 1 enrolment.", result.Message);
        Assert.Empty(_db.CourseGroups);
        Assert.Empty(_db.Enrolments);
    }

    // ---- Bundles ----

    [Fact]
    public async Task ABundle_NeedsCoursesFromThisOrganisation()
    {
        int mine = await AddCourseAsync("Acting I");
        await _courses.CreateAsync(_otherOrg, Course("Their course"));
        int theirs = _db.Courses.Single(c => c.OrganizationId == _otherOrg).Id;

        Assert.Equal("Choose at least one course for the bundle.", (await _courses.CreateBundleAsync(_org, new BundleInput("Year 1", null, []))).Error);
        Assert.Equal("Choose courses from this organisation.", (await _courses.CreateBundleAsync(_org, new BundleInput("Year 1", null, [mine, theirs]))).Error);
        Assert.True((await _courses.CreateBundleAsync(_org, new BundleInput("Year 1", null, [mine]))).Succeeded);
    }

    // ---- Enrolments ----

    [Fact]
    public async Task OnlyApprovedStudentsOfThisOrganisation_CanBeEnrolled()
    {
        int group = await AddGroupAsync(await AddCourseAsync("Acting I"), "A");
        ApplicationUser pending = AddMember("pending", _org, MembershipRole.Student, MembershipStatus.Pending);
        ApplicationUser otherStudent = AddMember("other", _otherOrg, MembershipRole.Student);

        Assert.False((await _enrolments.EnrolAsync(_org, pending.Id, group)).Succeeded);
        Assert.False((await _enrolments.EnrolAsync(_org, otherStudent.Id, group)).Succeeded);
        Assert.False((await _enrolments.EnrolAsync(_org, _teacher.Id, group)).Succeeded);
        Assert.Empty(_db.Enrolments);
    }

    [Fact]
    public async Task AFullGroup_IsRefused_AndEnrollingInAnotherGroupMovesTheStudent()
    {
        int courseId = await AddCourseAsync("Acting I");
        int a = await AddGroupAsync(courseId, "A", capacity: 1);
        int b = await AddGroupAsync(courseId, "B");
        ApplicationUser first = AddMember("first", _org, MembershipRole.Student);
        ApplicationUser second = AddMember("second", _org, MembershipRole.Student);
        await _enrolments.EnrolAsync(_org, first.Id, a);

        ServiceResult full = await _enrolments.EnrolAsync(_org, second.Id, a);
        ServiceResult moved = await _enrolments.EnrolAsync(_org, first.Id, b);

        Assert.StartsWith("Group A of Acting I is full (1 places)", full.Error);
        Assert.Equal("first Test moved from group A to group B of Acting I.", moved.Message);
        Assert.Equal(b, _db.Enrolments.AsNoTracking().Single().GroupId);
    }

    [Fact]
    public async Task APlacesLimit_CantGoBelowTheStudentsAlreadyInTheGroup()
    {
        int courseId = await AddCourseAsync("Acting I");
        int group = await AddGroupAsync(courseId, "A");
        await _enrolments.EnrolAsync(_org, AddMember("s1", _org, MembershipRole.Student).Id, group);
        await _enrolments.EnrolAsync(_org, AddMember("s2", _org, MembershipRole.Student).Id, group);

        ServiceResult result = await _courses.UpdateGroupAsync(_org, group, new GroupInput("A", 1, null));

        Assert.Equal("Group A already has 2 students; its places can't be fewer.", result.Error);
    }

    [Fact]
    public async Task EnrollingInABundle_KeepsTheCohortTogether_AndReportsWhatItCouldntDo()
    {
        int acting = await AddCourseAsync("Acting I");
        int voice = await AddCourseAsync("Voice");
        int history = await AddCourseAsync("History");
        int actingA = await AddGroupAsync(acting, "A");
        await AddGroupAsync(acting, "B", capacity: 20);
        int voiceA = await AddGroupAsync(voice, "A", capacity: 1);
        int voiceB = await AddGroupAsync(voice, "B");
        await _courses.CreateBundleAsync(_org, new BundleInput("Year 1", null, [acting, voice, history]));
        int bundle = _db.Bundles.Single().Id;
        await _enrolments.EnrolAsync(_org, AddMember("early", _org, MembershipRole.Student).Id, voiceA); // fills Voice A
        ApplicationUser student = AddMember("maria", _org, MembershipRole.Student);

        ServiceResult result = await _enrolments.EnrolInBundleAsync(_org, student.Id, bundle, "A");

        Assert.True(result.Succeeded);
        Assert.Equal("maria Test enrolled through Year 1 in Acting I (A), Voice (B). No group with free places: History.", result.Message);
        var places = _db.Enrolments.AsNoTracking().Where(e => e.StudentId == student.Id).ToList();
        Assert.Equal([actingA, voiceB], places.Select(e => e.GroupId).Order());
        Assert.All(places, e => Assert.Equal(bundle, e.BundleId));

        ServiceResult again = await _enrolments.EnrolInBundleAsync(_org, student.Id, bundle, null);
        Assert.False(again.Succeeded);
        Assert.Contains("Already taking: Acting I, Voice.", again.Error);
    }

    [Fact]
    public async Task AddingManyStudents_SkipsThoseAlreadyTakingTheCourse_AndRespectsPlaces()
    {
        int courseId = await AddCourseAsync("Acting I");
        int a = await AddGroupAsync(courseId, "A", capacity: 2);
        int b = await AddGroupAsync(courseId, "B");
        ApplicationUser s1 = AddMember("s1", _org, MembershipRole.Student);
        ApplicationUser s2 = AddMember("s2", _org, MembershipRole.Student);
        ApplicationUser s3 = AddMember("s3", _org, MembershipRole.Student);
        await _enrolments.EnrolAsync(_org, s1.Id, b);

        ServiceResult tooMany = await _enrolments.EnrolManyAsync(_org, a, [s2.Id, s3.Id, AddMember("s4", _org, MembershipRole.Student).Id]);
        ServiceResult ok = await _enrolments.EnrolManyAsync(_org, a, [s1.Id, s2.Id, s3.Id]);

        Assert.StartsWith("Group A has 2 free places; you chose 3 new students.", tooMany.Error);
        Assert.Equal("2 students added to Acting I, group A. 1 student already took the course and stayed in their group.", ok.Message);
        Assert.Equal(b, _db.Enrolments.AsNoTracking().Single(e => e.StudentId == s1.Id).GroupId);
    }

    [Fact]
    public async Task DeletingABundle_KeepsThePlacesItGave()
    {
        int courseId = await AddCourseAsync("Acting I");
        await AddGroupAsync(courseId, "A");
        await _courses.CreateBundleAsync(_org, new BundleInput("Year 1", null, [courseId]));
        int bundle = _db.Bundles.Single().Id;
        ApplicationUser student = AddMember("student", _org, MembershipRole.Student);
        await _enrolments.EnrolInBundleAsync(_org, student.Id, bundle, null);

        await _courses.DeleteBundleAsync(_org, bundle);

        Enrolment place = _db.Enrolments.AsNoTracking().Single();
        Assert.Null(place.BundleId);
        Assert.Equal(courseId, place.CourseId);
    }

    [Fact]
    public async Task AnotherOrganisationsEnrolment_CannotBeRemoved()
    {
        await _courses.CreateAsync(_otherOrg, Course("Theirs"));
        int theirCourse = _db.Courses.Single().Id;
        await _courses.CreateGroupAsync(_otherOrg, theirCourse, new GroupInput("A", 5, null));
        ApplicationUser theirStudent = AddMember("theirs", _otherOrg, MembershipRole.Student);
        await _enrolments.EnrolAsync(_otherOrg, theirStudent.Id, _db.CourseGroups.Single().Id);

        ServiceResult result = await _enrolments.UnenrolAsync(_org, _db.Enrolments.Single().Id);

        Assert.False(result.Succeeded);
        Assert.Single(_db.Enrolments);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }
}
