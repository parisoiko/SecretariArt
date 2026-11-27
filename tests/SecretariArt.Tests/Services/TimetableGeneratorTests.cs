using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using SecretariArt.Data;
using SecretariArt.Models;
using SecretariArt.Services;

namespace SecretariArt.Tests.Services;

// Milestone 5: the timetable generator (OR-Tools CP-SAT) and its drafts. Rules: Monday to Friday, 09:00–18:00, starts
// every 15 minutes, 15 minutes' changeover per room, at most 4 teaching hours a day. "Athens" has Acting (2 × 2 h) and
// Voice (1 × 1 h, needs a piano), both taught by one teacher and taken by one student, and Dance (2 × 90 min, needs
// mirrors, which no room has).
public sealed class TimetableGeneratorTests : IDisposable
{
    private static readonly TimeSpan Limit = TimeSpan.FromSeconds(10);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly SecretariArtDbContext _db;
    private readonly MovableClock _clock = new();
    private readonly RulesService _rules;
    private readonly ScheduleService _schedule;
    private readonly TimetableDraftService _drafts;
    private readonly GenerationSlots _slots;

    private readonly int _org;
    private readonly int _otherOrg;
    private readonly ApplicationUser _admin;
    private readonly ApplicationUser _teacher;
    private readonly ApplicationUser _student;
    private readonly int _hall;
    private readonly int _pianoRoom;
    private readonly int _actingA;
    private readonly int _voiceA;
    private readonly int _danceA;

    public TimetableGeneratorTests()
    {
        _connection.Open();
        _db = new SecretariArtDbContext(new DbContextOptionsBuilder<SecretariArtDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();

        var notifications = new NotificationService(_db, _clock);
        _rules = new RulesService(_db, _clock);
        _schedule = new ScheduleService(_db, _rules, notifications, _clock);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Generation:TimeLimitSeconds"] = "10" }).Build();
        _slots = new GenerationSlots(configuration);
        _drafts = new TimetableDraftService(_db, _schedule, notifications, _clock, configuration, _slots);

        _admin = AddUser("admin");
        _org = AddOrganization("Athens");
        _otherOrg = AddOrganization("Patras");
        Assert.True(_rules.SaveAsync(_org, new RulesInput(
            TeachingDays.Monday | TeachingDays.Tuesday | TeachingDays.Wednesday | TeachingDays.Thursday | TeachingDays.Friday,
            new TimeOnly(9, 0), new TimeOnly(18, 0), 15, 45, 180, 1, 3, 15, 4, null, null)).Result.Succeeded);

        _teacher = AddUser("teacher");
        _student = AddUser("student");
        _hall = AddRoom(_org, "Hall", 30);
        _pianoRoom = AddRoom(_org, "Piano room", 10, RoomFeatures.Piano);
        _actingA = AddGroup(_org, "Acting", 120, 2, RoomFeatures.None, _teacher);
        _voiceA = AddGroup(_org, "Voice", 60, 1, RoomFeatures.Piano, _teacher);
        _danceA = AddGroup(_org, "Dance", 90, 2, RoomFeatures.Mirrors, null);
        Enrol(_student, _actingA);
        Enrol(_student, _voiceA);
    }

    // ---- Setup helpers ----

    private ApplicationUser AddUser(string name)
    {
        var user = new ApplicationUser { UserName = $"{name}@test.example", Email = $"{name}@test.example", FirstName = name, LastName = "Test" };
        _db.Users.Add(user);
        _db.SaveChanges();
        return user;
    }

    private int AddOrganization(string name)
    {
        var organization = new Organization
        {
            Name = name, City = name, ContactEmail = "x@test.example", Status = OrganizationStatus.Active, InviteCode = name.ToUpperInvariant(),
            RequestedById = _admin.Id,
        };
        _db.Organizations.Add(organization);
        _db.SaveChanges();
        return organization.Id;
    }

    private int AddRoom(int organizationId, string name, int capacity, RoomFeatures features = RoomFeatures.None)
    {
        var room = new Classroom { OrganizationId = organizationId, Name = name, Capacity = capacity, Features = features };
        _db.Classrooms.Add(room);
        _db.SaveChanges();
        return room.Id;
    }

    private int AddGroup(int organizationId, string course, int minutes, int perWeek, RoomFeatures needs, ApplicationUser? teacher)
    {
        var c = new Course { OrganizationId = organizationId, Name = course, SessionMinutes = minutes, SessionsPerWeek = perWeek, RequiredFeatures = needs };
        var group = new CourseGroup { OrganizationId = organizationId, Course = c, Name = "A", Capacity = 10, TeacherId = teacher?.Id };
        _db.CourseGroups.Add(group);
        _db.SaveChanges();
        return group.Id;
    }

    private void Enrol(ApplicationUser student, int groupId)
    {
        CourseGroup group = _db.CourseGroups.Single(g => g.Id == groupId);
        _db.Enrolments.Add(new Enrolment { OrganizationId = group.OrganizationId, StudentId = student.Id, CourseId = group.CourseId, GroupId = groupId });
        _db.SaveChanges();
    }

    private void Place(int organizationId, int groupId, int roomId, DayOfWeek day, int hour, int minutes)
    {
        var starts = new TimeOnly(hour, 0);
        _db.ClassSessions.Add(new ClassSession
        {
            OrganizationId = organizationId, GroupId = groupId, ClassroomId = roomId, Day = day, Starts = starts, Ends = starts.AddMinutes(minutes),
        });
        _db.SaveChanges();
    }

    private async Task<TimetablePlan> PlanAsync(GenerationMode mode = GenerationMode.StartOver) =>
        TimetableGenerator.Plan(await _schedule.LoadContextAsync(_org), mode, Limit);

    // Every planned class, checked by the editor's own rules as if the plan were in place.
    private async Task<List<ScheduleCheck>> CheckAsync(TimetablePlan plan, GenerationMode mode)
    {
        ScheduleContext context = await _schedule.LoadContextAsync(_org);
        var planned = plan.Sessions.Select((p, i) => new ClassSession
        {
            Id = -(i + 1), OrganizationId = _org, GroupId = p.GroupId, ClassroomId = p.ClassroomId, Day = p.Day, Starts = p.Starts, Ends = p.Ends,
        }).ToList();
        ScheduleContext proposed = context.WithSessions((mode == GenerationMode.FillGaps ? context.Sessions : []).Concat(planned).ToList());
        return planned.Select(s => ScheduleChecker.Check(proposed, proposed.Group(s.GroupId)!, proposed.Room(s.ClassroomId)!, s.Day, s.Starts, s.Ends, s.Id)).ToList();
    }

    // ---- The generator ----

    [Fact]
    public async Task StartingOver_PlacesEveryClassItCan_WithinTheRules_AndExplainsTheRest()
    {
        TimetablePlan plan = await PlanAsync();

        Assert.Equal(2, plan.Sessions.Count(s => s.GroupId == _actingA));
        PlannedSession voice = Assert.Single(plan.Sessions, s => s.GroupId == _voiceA);
        Assert.Equal(_pianoRoom, voice.ClassroomId);
        Assert.All(await CheckAsync(plan, GenerationMode.StartOver), check => Assert.Empty(check.Problems));

        // Acting's two classes on different days, on the hour, inside the day.
        var acting = plan.Sessions.Where(s => s.GroupId == _actingA).ToList();
        Assert.NotEqual(acting[0].Day, acting[1].Day);
        Assert.All(plan.Sessions, s => Assert.True(s.Starts.Minute == 0 && s.Starts >= new TimeOnly(9, 0) && s.Ends <= new TimeOnly(18, 0)));

        PlanGap dance = Assert.Single(plan.Gaps);
        Assert.Equal((_danceA, 2), (dance.GroupId, dance.Missing));
        Assert.Equal("No classroom has mirrors, which Dance needs.", dance.Reason);
    }

    [Fact]
    public async Task FillingTheGaps_KeepsTheClassesThere_AndAddsOnlyWhatsMissing()
    {
        Place(_org, _actingA, _hall, DayOfWeek.Monday, 10, 120);

        TimetablePlan plan = await PlanAsync(GenerationMode.FillGaps);

        PlannedSession second = Assert.Single(plan.Sessions, s => s.GroupId == _actingA);
        Assert.NotEqual(DayOfWeek.Monday, second.Day);
        Assert.Single(plan.Sessions, s => s.GroupId == _voiceA);
        Assert.All(await CheckAsync(plan, GenerationMode.FillGaps), check => Assert.Empty(check.Problems));
    }

    [Fact]
    public async Task ATeacherTeachingElsewhere_IsOnlyGivenTheTimesTheyreFree()
    {
        // At Patras every weekday except Friday, all day: Voice and Acting can only be on Friday, one after the other.
        int patrasGroup = AddGroup(_otherOrg, "Patras class", 540, 4, RoomFeatures.None, _teacher);
        int patrasRoom = AddRoom(_otherOrg, "Studio", 20);
        foreach (DayOfWeek day in new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday })
        {
            Place(_otherOrg, patrasGroup, patrasRoom, day, 9, 540);
        }

        TimetablePlan plan = await PlanAsync();

        Assert.All(plan.Sessions.Where(s => s.GroupId == _voiceA || s.GroupId == _actingA), s => Assert.Equal(DayOfWeek.Friday, s.Day));
        // All five hours fit on Friday: the 4-hour limit and "different days" are preferences (warnings in the
        // editor), and placing a class comes first. The checks say so, as they would for a class placed by hand.
        Assert.Equal(3, plan.Sessions.Count);
        var checks = await CheckAsync(plan, GenerationMode.StartOver);
        Assert.All(checks, check => Assert.Empty(check.Problems));
        Assert.Contains(checks, check => check.Warnings.Any(w => w.Contains("more than the 4 h limit")));
    }

    [Fact]
    public async Task TheTeachersAvailability_IsPreferred()
    {
        _db.TeacherAvailability.AddRange(
            new TeacherAvailability { OrganizationId = _org, TeacherId = _teacher.Id, Day = DayOfWeek.Tuesday, From = new TimeOnly(9, 0), To = new TimeOnly(13, 0) },
            new TeacherAvailability { OrganizationId = _org, TeacherId = _teacher.Id, Day = DayOfWeek.Thursday, From = new TimeOnly(14, 0), To = new TimeOnly(18, 0) });
        _db.SaveChanges();

        TimetablePlan plan = await PlanAsync();

        // Acting twice (Tue morning, Thu afternoon), Voice fitted in next to one of them: no notes at all.
        Assert.Equal(3, plan.Sessions.Count);
        Assert.All(await CheckAsync(plan, GenerationMode.StartOver), check => Assert.DoesNotContain(check.Warnings, w => w.Contains("availability")));
    }

    [Fact]
    public async Task StudentsClassesElsewhere_AreAvoided_WhenNothingMoreImportantIsAtStake()
    {
        // The student takes a Patras class every weekday but Friday, all day: Friday is the only time without a student
        // clash. But the teacher's 4-hour limit and "a group once a day" rank above it (product order, 27 Nov): Friday
        // gets one Acting class and Voice (3 h), and the second Acting class takes the student clash on another day.
        int patrasGroup = AddGroup(_otherOrg, "Patras class", 540, 4, RoomFeatures.None, null);
        int patrasRoom = AddRoom(_otherOrg, "Studio", 20);
        Enrol(_student, patrasGroup);
        foreach (DayOfWeek day in new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday })
        {
            Place(_otherOrg, patrasGroup, patrasRoom, day, 9, 540);
        }

        TimetablePlan plan = await PlanAsync();

        Assert.Equal(3, plan.Sessions.Count);
        Assert.Equal(DayOfWeek.Friday, plan.Sessions.Single(s => s.GroupId == _voiceA).Day);
        var acting = plan.Sessions.Where(s => s.GroupId == _actingA).Select(s => s.Day).ToList();
        Assert.Contains(DayOfWeek.Friday, acting);
        Assert.NotEqual(acting[0], acting[1]);
        var checks = await CheckAsync(plan, GenerationMode.StartOver);
        Assert.Single(checks, check => check.Warnings.Any(w => w.Contains("another organisation")));
        Assert.DoesNotContain(checks, check => check.Warnings.Any(w => w.Contains("4 h limit")));
    }

    [Fact]
    public async Task AClassThatCantBePlaced_SaysWhatBlocksIt()
    {
        // The teacher is at Patras every teaching hour of the week: nothing of theirs fits, and the reason says why.
        int patrasGroup = AddGroup(_otherOrg, "Patras class", 540, 5, RoomFeatures.None, _teacher);
        int patrasRoom = AddRoom(_otherOrg, "Studio", 20);
        foreach (DayOfWeek day in new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday })
        {
            Place(_otherOrg, patrasGroup, patrasRoom, day, 9, 540);
        }

        TimetablePlan plan = await PlanAsync();

        PlanGap voice = Assert.Single(plan.Gaps, g => g.GroupId == _voiceA);
        Assert.StartsWith("No time works: teacher Test teaches at another organisation at the times left. For example: teacher Test teaches at another organisation on", voice.Reason);
        Assert.DoesNotContain("by hand", voice.Reason);
    }

    [Fact]
    public async Task ATeachersClassesAreSpreadOverTheWeek_AndALoneClassGetsAnOrdinaryTime()
    {
        // Four one-hour groups with the same teacher and no students in common: one a day rather than all on one.
        ApplicationUser busy = AddUser("busy");
        var groups = Enumerable.Range(1, 4).Select(i => AddGroup(_otherOrg, $"Solo {i}", 60, 1, RoomFeatures.None, busy)).ToList();
        AddRoom(_otherOrg, "Studio", 20);
        TimetablePlan spread = TimetableGenerator.Plan(await _schedule.LoadContextAsync(_otherOrg), GenerationMode.StartOver, Limit);
        Assert.Equal(4, spread.Sessions.Select(s => s.Day).Distinct().Count());

        // One class in an empty week (default rules: Monday to Saturday, 09:00–22:00): not in the last hour.
        ScheduleContext context = await _schedule.LoadContextAsync(_otherOrg);
        ScheduleContext one = context.WithSessions([]);
        var lone = TimetableGenerator.Plan(new ScheduleContext
        {
            Organization = one.Organization, Rules = one.Rules, Rooms = one.Rooms, Groups = one.Groups.Where(g => g.Id == groups[0]).ToList(),
            Sessions = [], Availability = [], TaughtElsewhere = [], AttendedElsewhere = [], OtherRules = one.OtherRules,
        }, GenerationMode.StartOver, Limit).Sessions.Single();
        Assert.True(lone.Ends <= new TimeOnly(21, 0), $"Ends at {lone.Ends}");
    }

    // ---- Drafts ----

    [Fact]
    public async Task ADraft_ChangesNothingUntilUsed_ThenReplacesTheTimetable_AndTellsEachPersonOnce()
    {
        Place(_org, _actingA, _hall, DayOfWeek.Monday, 10, 120);

        ServiceResult generated = await _drafts.GenerateAsync(_org, GenerationMode.StartOver, _admin.Id);

        Assert.True(generated.Succeeded, generated.Error);
        Assert.Contains("3 classes placed, 2 couldn't be", generated.Message);
        Assert.Single(_db.ClassSessions.Where(s => s.OrganizationId == _org));
        DraftView view = (await _drafts.GetAsync(_org))!;
        Assert.True(view.CanUse);
        Assert.Equal(3, view.NewCount);
        Assert.Equal("Dance", Assert.Single(view.Gaps).Group.Course!.Name);

        ServiceResult used = await _drafts.UseAsync(_org);

        Assert.Equal("The new timetable is in place: 3 weekly classes, replacing 1. Teachers and students have been told.", used.Message);
        Assert.Equal(3, _db.ClassSessions.Count(s => s.OrganizationId == _org));
        Assert.False(await _drafts.ExistsAsync(_org));
        string notice = Assert.Single(_db.Notifications.Where(n => n.UserId == _student.Id).Select(n => n.Text));
        Assert.StartsWith("Athens: The timetable has been rebuilt. Your classes: ", notice);
        Assert.Contains("(group A)", notice);
        Assert.Contains(" in Hall", notice);
        Assert.Equal(2, Regex.Matches(notice, "Acting ").Count);
        Assert.Contains("Voice ", notice);
        Assert.Single(_db.Notifications.Where(n => n.UserId == _teacher.Id));
    }

    [Fact]
    public async Task ADraftThatNoLongerFits_IsRefused()
    {
        await _drafts.GenerateAsync(_org, GenerationMode.FillGaps, _admin.Id);
        DraftSession voice = _db.DraftSessions.AsNoTracking().Single(s => s.GroupId == _voiceA);

        // Meanwhile someone puts Acting in the piano room at the very time the draft gives Voice.
        Place(_org, _actingA, _pianoRoom, voice.Day, voice.Starts.Hour, 120);
        DraftView view = (await _drafts.GetAsync(_org))!;
        ServiceResult used = await _drafts.UseAsync(_org);

        Assert.False(view.CanUse);
        Assert.StartsWith("The timetable has changed since this draft was made, so it no longer fits:", used.Error);
        Assert.Single(_db.ClassSessions.Where(s => s.OrganizationId == _org));
    }

    [Fact]
    public async Task WhenEverySlotIsTaken_GenerationSaysTheServerIsBusy()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Generation:MaxConcurrent"] = "1", ["Generation:WaitSeconds"] = "0",
        }).Build();
        using var slots = new GenerationSlots(configuration);
        var drafts = new TimetableDraftService(_db, _schedule, new NotificationService(_db, _clock), _clock, configuration, slots);
        Assert.True(await slots.WaitAsync(CancellationToken.None));  // someone else is generating

        ServiceResult result = await drafts.GenerateAsync(_org, GenerationMode.StartOver, _admin.Id);

        Assert.Equal("Other timetables are being generated right now. Try again in a minute.", result.Error);
        Assert.False(_db.ScheduleDrafts.Any());
    }

    [Fact]
    public async Task WhenTheAdminLeaves_GenerationStopsAndSavesNothing()
    {
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            _drafts.GenerateAsync(_org, GenerationMode.StartOver, _admin.Id, new CancellationToken(canceled: true)));
        Assert.False(_db.ScheduleDrafts.Any());
    }

    [Fact]
    public async Task ADraftWithADeletedGroup_NamesTheGroup()
    {
        await _drafts.GenerateAsync(_org, GenerationMode.FillGaps, _admin.Id);
        Assert.Contains(_db.DraftSessions.AsNoTracking(), s => s.GroupId == _voiceA);

        // Meanwhile Voice A is deleted (with its enrolment).
        _db.Enrolments.RemoveRange(_db.Enrolments.Where(e => e.GroupId == _voiceA));
        _db.CourseGroups.Remove(_db.CourseGroups.Single(g => g.Id == _voiceA));
        await _db.SaveChangesAsync();
        ServiceResult used = await _drafts.UseAsync(_org);

        Assert.Contains("Voice (group A) has been deleted since (1 class in the draft).", used.Error);
    }

    [Fact]
    public async Task NothingToGenerate_SaysWhatsMissing()
    {
        Assert.Equal("There are no groups to place yet: add courses with groups on the Courses tab first.",
            (await _drafts.GenerateAsync(_otherOrg, GenerationMode.StartOver, _admin.Id)).Error);
    }

    public void Dispose()
    {
        _slots.Dispose();
        _db.Dispose();
        _connection.Dispose();
    }
}
